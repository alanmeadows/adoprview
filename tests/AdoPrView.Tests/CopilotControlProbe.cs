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
        var result = await new WslCopilotRunTransport().ExecuteAsync(new("fixture-control",
            distro,
            """exec timeout --signal=TERM --kill-after=5s 20s python3 -I - "$1" """,
            [CopilotRunService.RunnerScript.Replace("\r\n", "\n")], Controller),
            null, null, deadline.Token);
        if (result.ExitCode != 0)
            throw new Exception($"Disposable WSL runner fixture failed (exit {result.ExitCode}); no existing jobs were used.");
        using var report = JsonDocument.Parse(result.StandardOutput);
        foreach (var name in new[] { "escalated", "freeLock", "orphanLockHeld", "foreignReceiptRefused", "workAndLogPreserved", "cleaned" })
            if (!report.RootElement.GetProperty(name).GetBoolean())
                throw new Exception("Disposable WSL runner check failed: " + name);
    }

    const string Controller = """"
        import fcntl,json,os,pathlib,shutil,signal,subprocess,sys,tempfile,time,uuid
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
            except FileNotFoundError: return None
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
            os.kill(value["pid"],signal.SIGKILL)
        def wait_file(path):
            end=time.monotonic()+4
            while not path.exists():
                if time.monotonic()>end: raise TimeoutError("fixture readiness expired")
                time.sleep(.02)
            return json.loads(path.read_text())
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
        def launch(mode,foreign=False):
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
                    env={**os.environ,"BASH_ENV":"/dev/null"})
            children.append(child)
            current=identity(child.pid)
            if current is not None: remember(current)
            return run,child,nonce,session
        report={}
        try:
            run,child,nonce,session=launch("stubborn")
            leader=remember(wait_file(run/"leader.json"))
            descendant=remember(wait_file(run/"descendant.json"))
            assert leader["group"]==leader["pid"]==leader["session"]
            assert descendant["group"]==descendant["session"]==leader["pid"]
            assert not lock_free(run)
            started=time.monotonic()
            child.send_signal(signal.SIGTERM)
            assert child.wait(timeout=4)==143
            assert time.monotonic()-started<4
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
        finally:
            for value in reversed(owned): stop_owned(value)
            for child in children: child.wait(timeout=2)
            end=time.monotonic()+2
            while any(alive(value) for value in owned):
                if time.monotonic()>end: raise TimeoutError("owned fixture cleanup unconfirmed")
                time.sleep(.02)
            shutil.rmtree(root)
        report["cleaned"]=not root.exists()
        print(json.dumps(report))
        """";
}
