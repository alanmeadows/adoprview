using System.Text.Json;
using AdoPrView;

static class CopilotControlProbe
{
    public static async Task RunAsync()
    {
        var distro = Environment.GetEnvironmentVariable("ADOPRVIEW_TEST_WSL_DISTRO");
        if (string.IsNullOrWhiteSpace(distro))
            throw new Exception("Select an already-responsive distribution with ADOPRVIEW_TEST_WSL_DISTRO before running the disposable fixture.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        foreach (var operation in new[] { "os.pidfd_open", "signal.pidfd_send_signal" })
        {
            var failure = operation == "os.pidfd_open"
                ? """OSError(38,"synthetic unavailable pidfd syscall")"""
                : """PermissionError(1,"synthetic denied pidfd signal")""";
            var script = $$"""
                import os,signal,subprocess,tempfile
                def blocked(*args): raise {{failure}}
                def forbidden(*args,**kwargs): raise AssertionError("preflight created fixture resources")
                {{operation}}=blocked
                tempfile.mkdtemp=forbidden
                subprocess.Popen=forbidden
                """ + "\n" + Controller;
            var rejected = await new WslCopilotRunTransport().ExecuteAsync(new("fixture-control-preflight",
                distro, """exec timeout --signal=TERM --kill-after=2s 5s python3 -I - "$1" """,
                [CopilotRunService.RunnerScript.Replace("\r\n", "\n")], script),
                null, null, deadline.Token);
            if (rejected.ExitCode == 0 ||
                !rejected.StandardError.Contains("requires available and permitted pidfd operations; no helpers launched") ||
                rejected.StandardError.Contains("preflight created fixture resources"))
                throw new Exception("Disposable pidfd prerequisite did not reject " + operation + " before fixture creation.");
        }
        Console.WriteLine("PIDFD_PREFLIGHT unavailableRefused=true deniedRefused=true resourcesCreated=false");
        var result = await new WslCopilotRunTransport().ExecuteAsync(new("fixture-control",
            distro,
            """exec timeout --signal=TERM --kill-after=5s 20s python3 -I - "$1" """,
            [CopilotRunService.RunnerScript.Replace("\r\n", "\n")], Controller),
            null, null, deadline.Token);
        if (result.ExitCode != 0)
            throw new Exception($"Disposable WSL runner fixture failed (exit {result.ExitCode}); no existing jobs were used.");
        using var report = JsonDocument.Parse(result.StandardOutput);
        foreach (var name in new[] { "escalated", "freeLock", "orphanLockHeld", "foreignReceiptRefused",
            "staleIdentityRefused", "missingIdentityRefused", "earlyStopReplayed", "readinessFailureCleaned",
            "workAndLogPreserved", "pidfdPreflightVerified", "cleaned" })
            if (!report.RootElement.GetProperty(name).GetBoolean())
                throw new Exception("Disposable WSL runner check failed: " + name);
        Console.WriteLine("KERNEL_CONTROL " + result.StandardOutput.Trim());
    }

    const string Controller = """"
        import ctypes,fcntl,json,os,pathlib,shutil,signal,subprocess,sys,tempfile,time,uuid
        if not hasattr(os,"pidfd_open") or not hasattr(signal,"pidfd_send_signal"):
            raise RuntimeError("Disposable cleanup requires Linux pidfd support; no helpers launched")
        if ctypes.CDLL(None,use_errno=True).prctl(36,1,0,0,0)!=0:
            raise RuntimeError("Disposable cleanup requires a child subreaper; no helpers launched")
        try:
            handle=os.pidfd_open(os.getpid(),0)
            try: signal.pidfd_send_signal(handle,0)
            finally: os.close(handle)
        except OSError as error:
            raise RuntimeError("Disposable cleanup requires available and permitted pidfd operations; no helpers launched") from error
        runner_script=sys.argv[1]
        root=pathlib.Path(tempfile.mkdtemp(prefix="ado-control-probe-"))
        owned=[]
        children=[]
        def identity(pid):
            try:
                text=pathlib.Path(f"/proc/{pid}/stat").read_text()
                values=text[text.rfind(") ")+2:].split()
                return {"pid":pid,"state":values[0],"group":int(values[2]),"session":int(values[3]),
                    "start":values[19],"uid":pathlib.Path(f"/proc/{pid}").stat().st_uid}
            except (FileNotFoundError,ProcessLookupError): return None
        def remember(value):
            assert value["uid"]==os.getuid()
            owned.append(value)
            return value
        def alive(value):
            current=identity(value["pid"])
            return current is not None and current["start"]==value["start"] and current["state"]!="Z"
        def stop_owned(value):
            current=identity(value["pid"])
            if current is None or current["state"]=="Z": return
            assert all(current[key]==value[key] for key in ("start","group","session","uid")), "fixture PID identity changed"
            try: handle=os.pidfd_open(value["pid"],0)
            except ProcessLookupError: return
            try:
                current=identity(value["pid"])
                if current is None or current["state"]=="Z": return
                assert all(current[key]==value[key] for key in ("start","group","session","uid")), "fixture PID identity changed"
                try: signal.pidfd_send_signal(handle,signal.SIGKILL)
                except ProcessLookupError: pass
            finally: os.close(handle)
        def wait_file(path,seconds=4):
            end=time.monotonic()+seconds
            while not path.exists():
                if time.monotonic()>end: raise TimeoutError("fixture readiness expired")
                time.sleep(.02)
            return json.loads(path.read_text())
        def cleanup_children():
            signal.signal(signal.SIGTERM,signal.SIG_IGN)
            end=time.monotonic()+2
            child_file=pathlib.Path(f"/proc/{os.getpid()}/task/{os.getpid()}/children")
            while True:
                # Only this subreaper's descendants are eligible, independent of readiness files.
                for pid in map(int,child_file.read_text().split()):
                    before=identity(pid)
                    if before is None or before["state"]=="Z": continue
                    assert before["uid"]==os.getuid()
                    try: handle=os.pidfd_open(pid,0)
                    except ProcessLookupError: continue
                    try:
                        after=identity(pid)
                        if after is None or after["state"]=="Z": continue
                        assert all(before[key]==after[key] for key in ("uid","start","group","session"))
                        remember(after)
                        try: signal.pidfd_send_signal(handle,signal.SIGKILL)
                        except ProcessLookupError: pass
                    finally: os.close(handle)
                while True:
                    try: reaped,_=os.waitpid(-1,os.WNOHANG)
                    except ChildProcessError: break
                    if reaped==0: break
                if not child_file.read_text().split(): break
                if time.monotonic()>end: raise TimeoutError("disposable descendant cleanup unconfirmed")
                time.sleep(.01)
            assert not any(alive(value) for value in owned), "owned helper survived cleanup"
        def lock_free(run):
            with (run/"task.lock").open("a") as lock:
                try: fcntl.flock(lock,fcntl.LOCK_EX|fcntl.LOCK_NB)
                except BlockingIOError: return False
                return True
        def interrupted(signum,frame): raise TimeoutError("fixture deadline expired")
        signal.signal(signal.SIGTERM,interrupted)
        cli=root/"fixture-cli"
        cli.write_text("#!"+sys.executable+"\n"+r'''
        import json,os,pathlib,signal,subprocess,sys,time
        root=pathlib.Path.cwd()
        def record():
            text=pathlib.Path(f"/proc/{os.getpid()}/stat").read_text()
            values=text[text.rfind(") ")+2:].split()
            return {"pid":os.getpid(),"state":values[0],"group":os.getpgrp(),"session":os.getsid(0),
                "start":values[19],"uid":os.getuid()}
        signal.signal(signal.SIGTERM,signal.SIG_IGN)
        code=r"""
        import json,os,pathlib,signal,time
        signal.signal(signal.SIGTERM,signal.SIG_IGN)
        text=pathlib.Path(f"/proc/{os.getpid()}/stat").read_text()
        values=text[text.rfind(") ")+2:].split()
        pathlib.Path("descendant.json.tmp").write_text(json.dumps({"pid":os.getpid(),"state":values[0],
            "group":os.getpgrp(),"session":os.getsid(0),"start":values[19],"uid":os.getuid()}))
        pathlib.Path("descendant.json.tmp").replace("descendant.json")
        time.sleep(30)
        """
        subprocess.Popen([sys.executable,"-I","-c",code],close_fds=False)
        end=time.monotonic()+3
        while not (root/"descendant.json").exists():
            if time.monotonic()>end: sys.exit(2)
            time.sleep(.01)
        (root/"leader.json.tmp").write_text(json.dumps(record()))
        (root/"leader.json.tmp").replace(root/"leader.json")
        print("SYNTHETIC CLI READY",flush=True)
        if (root/"mode").read_text()=="orphan": sys.exit(0)
        time.sleep(30)
        ''')
        cli.chmod(0o700)
        hook=root/"early-stop.sh"
        hook.write_text(r'''trap 'if [[ "$BASH_COMMAND" == '\''child_identity="$(read_cli_identity)"'\'' ]]; then trap - DEBUG; kill -TERM "$$"; fi' DEBUG'''+"\n")
        def launch(mode,foreign=False,early=False):
            run=root/mode
            run.mkdir()
            (run/"worktree").mkdir()
            (run/"worktree"/"retained.txt").write_text("synthetic work")
            (run/"mode").write_text(mode)
            nonce=uuid.uuid4().hex*2
            session=str(uuid.uuid4())
            (run/"ownership").write_text("\n".join([str(root),"fixture-branch","1"*40,nonce,session])+"\n")
            (run/"prompt").write_text("Synthetic fixture only; no model or PR.")
            with (run/"fixture.log").open("wb") as log:
                child=subprocess.Popen(["bash","--noprofile","--norc","-c",runner_script,"fixture-runner",
                    str(cli),str(run),session,uuid.uuid4().hex*2 if foreign else nonce,"start"],
                    cwd=run,stdin=subprocess.DEVNULL,stdout=log,stderr=log,start_new_session=True,
                    env={**os.environ,"BASH_ENV":str(hook) if early else "/dev/null"})
            children.append(child)
            current=identity(child.pid)
            if current is not None: remember(current)
            return run,child,nonce,session
        report={"pidfdPreflightVerified":True}
        try:
            # Exercise the verbatim production signal guards with stale/absent
            # captured identities, without trying to force real host PID reuse.
            begin=runner_script.index("read_cli_identity() {")
            end=runner_script.index("trap stop_cli TERM INT\n",begin)
            guards=runner_script[begin:end]
            guard_script="set -euo pipefail\n"+guards+r'''
        child=""; child_identity=""; interrupted=0
        cleanup() {
          child_identity="$original"
          if owns_cli_group; then kill -KILL -- "-$child" 2>/dev/null || true; fi
          wait "$child" 2>/dev/null || true
        }
        setsid python3 -I -c 'import json,os,pathlib,sys,time; p=os.getpid(); v=pathlib.Path(f"/proc/{p}/stat").read_text().rsplit(") ",1)[1].split(); out=pathlib.Path(sys.argv[1]); tmp=out.with_suffix(".tmp"); tmp.write_text(json.dumps({"pid":p,"state":v[0],"group":int(v[2]),"session":int(v[3]),"start":v[19],"uid":pathlib.Path(f"/proc/{p}").stat().st_uid})); tmp.replace(out); time.sleep(15)' "$1/guard.json" & child=$!
        for attempt in {1..10}; do child_identity="$(read_cli_identity)" && break; sleep .01; done
        original="$child_identity"
        [[ -n "$original" ]] || exit 2
        trap cleanup EXIT
        for attempt in {1..200}; do [[ -f "$1/guard.json" ]] && break; sleep .01; done
        [[ -f "$1/guard.json" ]] || exit 2
        child_identity="${original%:*}:0"
        stop_cli
        kill -0 "$child"
        child_identity=""
        stop_cli
        kill -0 "$child"
        printf 'GUARDS_REFUSED\n'
        '''
            guard=subprocess.Popen(["bash","--noprofile","--norc","-c",guard_script,"fixture-guards",str(root)],
                stdin=subprocess.DEVNULL,stdout=subprocess.PIPE,stderr=subprocess.PIPE,start_new_session=True,
                env={**os.environ,"BASH_ENV":"/dev/null"})
            children.append(guard)
            current=identity(guard.pid)
            if current is not None: remember(current)
            remember(wait_file(root/"guard.json"))
            stdout,stderr=guard.communicate(timeout=4)
            assert guard.returncode==0 and stdout==b"GUARDS_REFUSED\n", "production guards signalled a stale/absent identity"
            report["staleIdentityRefused"]=True
            report["missingIdentityRefused"]=True
            run,child,nonce,session=launch("stubborn")
            leader=remember(wait_file(run/"leader.json"))
            descendant=remember(wait_file(run/"descendant.json"))
            assert leader["group"]==leader["pid"]==leader["session"]
            assert descendant["group"]==descendant["session"]==leader["pid"]
            assert not lock_free(run)
            started=time.monotonic()
            child.send_signal(signal.SIGTERM)
            assert child.wait(timeout=4)==143
            report["escalationMs"]=round((time.monotonic()-started)*1000,1)
            assert report["escalationMs"]<4000
            assert not alive(leader) and not alive(descendant), "TERM-ignoring owned group survived"
            assert (run/"outcome").read_text().splitlines()==[nonce,session,"143"]
            report["escalated"]=True
            report["freeLock"]=lock_free(run)
            report["workAndLogPreserved"]=(run/"worktree"/"retained.txt").read_text()=="synthetic work" and \
                "SYNTHETIC CLI READY" in (run/"fixture.log").read_text()
            run,child,nonce,session=launch("orphan")
            remember(wait_file(run/"leader.json"))
            descendant=remember(wait_file(run/"descendant.json"))
            assert child.wait(timeout=4)==0
            report["orphanLockHeld"]=alive(descendant) and not lock_free(run)
            stop_owned(descendant)
            run,child,nonce,session=launch("foreign",foreign=True)
            report["foreignReceiptRefused"]=child.wait(timeout=3)==76 and not (run/"leader.json").exists()
            run,child,nonce,session=launch("early",early=True)
            report["earlyStopReplayed"]=child.wait(timeout=4)==143 and lock_free(run) and \
                (run/"outcome").read_text().splitlines()==[nonce,session,"143"]
            run,child,nonce,session=launch("unready")
            unregistered=wait_file(run/"descendant.json")
            assert alive(unregistered) and unregistered not in owned
            try:
                wait_file(run/"never-ready.json",seconds=.1)
                raise AssertionError("missing readiness unexpectedly succeeded")
            except TimeoutError: pass
        finally:
            cleanup_children()
            for child in children: child.wait(timeout=2)
            shutil.rmtree(root)
        report["readinessFailureCleaned"]=not alive(unregistered)
        report["cleaned"]=not root.exists()
        print(json.dumps(report))
        """";
}
