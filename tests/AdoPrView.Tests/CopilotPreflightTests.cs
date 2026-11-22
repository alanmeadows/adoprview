using AdoPrView;
using System.Diagnostics;
using System.Text.Json;

static class CopilotPreflightTests
{
    const string Help = "--no-ask-user --stream --no-color --deny-tool --yolo --session-id --resume";

    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    static string Failure(CopilotProcessResult result)
    {
        try { CopilotRunService.RequirePreflightSuccess(result); }
        catch (Exception ex) { return ex.Message; }
        throw new Exception("Failed preflight was accepted");
    }

    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        foreach (var (stage, detail) in new[]
        {
            ("environment", "environment"), ("checkout", "directory"), ("git", "Git"),
            ("copilot", "~/.local/bin/copilot"), ("bgtask", "~/go/bin/bgtask"),
            ("flock", "flock"), ("setsid", "setsid"), ("python3", "python3"),
            ("copilot-help", "copilot --help"), ("bgtask-help", "bgtask --help"),
            ("copilot-home", "COPILOT_HOME"), ("distribution", "WSL_DISTRO_NAME"), ("remotes", "remote")
        })
            await test("WSL preflight safe stage diagnostic: " + stage, () =>
            {
                var message = Failure(new(73, "https://secret.invalid/token", $"secret stderr\nADOPRVIEW_PREFLIGHT:{stage}:73\n"));
                Check(message.Contains(detail) && message.Contains("exit 73") && !message.Contains("secret"),
                    "Actionable stage/exit missing or raw process data leaked");
                return Task.CompletedTask;
            });

        await test("WSL preflight rejects unrecognized, malformed and wrong-exit diagnostics without exposing process data", () =>
        {
            foreach (var stderr in new[]
            {
                "", "ADOPRVIEW_PREFLIGHT:secret-token:1\n", "ADOPRVIEW_PREFLIGHT:copilot:73\n",
                "ADOPRVIEW_PREFLIGHT:copilot:1:secret\n", "ADOPRVIEW_PREFLIGHT:secret:999999999999999\n"
            })
            {
                var message = Failure(new(1, "secret output", stderr));
                Check(message.Contains("environment") && message.Contains("exit 1") &&
                    !message.Contains("secret") && !message.Contains("~/.local/bin"),
                    "Untrusted diagnostic accepted or raw output exposed");
            }
            Check(Failure(new(124, "", "")).Contains("exit 124"), "Transport timeout exit was lost");
            CopilotRunService.RequirePreflightSuccess(new(0, "", "secret stderr"));
            return Task.CompletedTask;
        });

        if (Environment.GetEnvironmentVariable("ADOPRVIEW_TEST_WSL_PREFLIGHT") == "1")
        {
            await test("WSL preflight non-login executable discovery, quoted paths, child PATH and safe failures", RunWsl);
            await test("WSL captured task registry controls avoid changed environment setup", RunRegistry);
        }
    }

    static async Task RunRegistry()
    {
        var native = new WslCopilotRunTransport();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        Task<CopilotProcessResult> Shell(string script, params string[] args) =>
            native.ExecuteAsync(new("registry-fixture", "Ubuntu", script, args), null, null, deadline.Token);
        var created = await Shell("mktemp -d /tmp/ado-prview-registry.XXXXXXXXXX");
        Check(created.ExitCode == 0, "Could not create disposable registry fixture");
        var directory = created.StandardOutput.Trim();
        Check(System.Text.RegularExpressions.Regex.IsMatch(directory, @"\A/tmp/ado-prview-registry\.[a-zA-Z0-9]+\z"),
            "Invalid registry fixture directory");
        try
        {
            var home = directory + "/home 'quoted";
            var checkout = home + "/source";
            var setup = await Shell("""
                set -euo pipefail
                mkdir -p -- "$1/.config/bash" "$1/.local/bin" "$1/go/bin" "$2/worktree"
                git init -q -- "$2"
                git -C "$2" remote add origin https://dev.azure.com/offline-org/offline-project/_git/offline-repo
                printf '#!/bin/bash\nprintf "%%s\\n" "%s"\n' "$3" > "$1/.local/bin/copilot"
                cat > "$1/go/bin/bgtask" <<'PY'
                #!/usr/bin/python3
                import json, os, pathlib, sys
                a = sys.argv[1:]
                if a == ["--help"]:
                    sys.exit(0)
                root = os.environ.get("BGTASK_STATE_DIR") or os.path.join(
                    os.environ.get("XDG_CONFIG_HOME") or os.path.join(os.environ["HOME"], ".config"), "bgtask")
                if a[0] == "run":
                    name, cwd = a[2], a[4]
                    p = pathlib.Path(root) / "procs" / name
                    p.mkdir(parents=True)
                    task = dict(id="registry-fixture", name=name, cwd=cwd, command=a[6:],
                                status=dict(state="running"))
                    (p / "meta.json").write_text(json.dumps(task))
                    (p / "output").write_text("PRESERVED-REGISTRY-OUTPUT\n")
                    sys.exit(0)
                p = pathlib.Path(root) / "procs" / a[1]
                if not (p / "meta.json").is_file():
                    sys.exit(84)
                task = json.loads((p / "meta.json").read_text())
                if a[0] == "stop":
                    task["status"] = dict(state="exited", exited=dict(code=143))
                    (p / "meta.json").write_text(json.dumps(task))
                elif a[0] == "status":
                    print(json.dumps(task))
                elif a[0] == "logs":
                    print((p / "output").read_text(), end="")
                else:
                    sys.exit(85)
                PY
                chmod +x -- "$1/.local/bin/copilot" "$1/go/bin/bgtask"
                printf 'fixture-owned-work\n' > "$2/worktree/preserved"
                """, home, checkout, Help);
            Check(setup.ExitCode == 0, "Could not initialize fake registry supervisor");
            await using var service = new CopilotRunService(new RegistryTransport(native, home));
            var request = new CopilotRunRequest("offline:1", 1, "Offline registry fixture", "offline-org",
                "offline-project", "offline-repo", "offline", "refs/heads/offline", new string('1', 40), "");
            foreach (var variant in new[] { "default", "xdg", "override" })
            {
                var config = variant == "default" ? home + "/.config" : home + "/config 'selected";
                var state = variant == "override" ? "relative 'registry" : "";
                Check((await Shell("""
                    printf 'export XDG_CONFIG_HOME=%q\nexport BGTASK_STATE_DIR=%q\n' "$2" "$3" > "$1/.config/bash/environment"
                    """, home, variant == "default" ? "" : config, state)).ExitCode == 0, "Could not select fixture registry");
                var preflight = await service.Invoke("preflight", "Ubuntu", CopilotRunService.PreflightScript,
                    [checkout, "offline-repo"], token: deadline.Token);
                CopilotRunService.RequirePreflightSuccess(preflight);
                var env = CopilotRunService.ParseEnvironment(preflight.StandardOutput, request);
                Check(env.XdgConfigHome == config &&
                    env.BgtaskStateDirectory == (state.Length == 0 ? "" : checkout + "/" + state),
                    "Actual preflight did not capture absolute XDG/override registry context");
                var run = home + "/run-" + variant;
                Check((await Shell("mkdir -- \"$1\"; touch -- \"$1/ownership\"; printf 'private fixture prompt' > \"$1/prompt\"", run)).ExitCode == 0,
                    "Could not create fake launch receipt");
                var name = "registry-" + variant;
                var launched = await service.Invoke("launch", "Ubuntu", CopilotRunService.LaunchScript,
                    [env.Bgtask, name, checkout + "/worktree", env.Copilot, run,
                        CopilotRunService.RunnerScript.Replace("\r\n", "\n"), "offline-session", "offline-nonce", "start"],
                    token: deadline.Token, environment: env);
                Check(launched.ExitCode == 0, "Production launch request failed against the fake registry supervisor");
                Check((await Shell("""
                    printf 'printf "unexpected-environment-source\\n" > "$HOME/poison-read"\nexit 79\n' > "$1/.config/bash/environment"
                    """, home)).ExitCode == 0, "Could not isolate changed environment setup");
                var elapsed = Stopwatch.StartNew();
                var before = await service.Invoke("status", "Ubuntu", CopilotRunService.StatusScript,
                    [env.Bgtask, name], token: deadline.Token, environment: env);
                Check(before.ExitCode == 0, "Actual control transport could not find the launch registry");
                using var beforeTask = JsonDocument.Parse(before.StandardOutput);
                Check(beforeTask.RootElement
                    .GetProperty("status").GetProperty("state").GetString() == "running",
                    "Actual control transport could not find the launch registry");
                var stopped = await service.Invoke("stop", "Ubuntu", CopilotRunService.StopScript,
                    [env.Bgtask, name], token: deadline.Token, environment: env);
                var after = await service.Invoke("status", "Ubuntu", CopilotRunService.StatusScript,
                    [env.Bgtask, name], token: deadline.Token, environment: env);
                using var task = JsonDocument.Parse(after.StandardOutput);
                Check(stopped.ExitCode == 0 && after.ExitCode == 0 &&
                    task.RootElement.GetProperty("id").GetString() == "registry-fixture" &&
                    task.RootElement.GetProperty("status").GetProperty("exited").GetProperty("code").GetInt32() == 143 &&
                    elapsed.Elapsed < TimeSpan.FromSeconds(3), "Captured-registry fake stop was not bounded and exact");
                var stopMs = elapsed.Elapsed.TotalMilliseconds;
                var logs = await service.Invoke("history", "Ubuntu", "\"$1\" logs \"$2\" --tail 2000",
                    [env.Bgtask, name], token: deadline.Token, environment: env);
                Check(logs.ExitCode == 0 && logs.StandardOutput == "PRESERVED-REGISTRY-OUTPUT\n" &&
                    (await Shell("[[ ! -e \"$1/poison-read\" && -f \"$2/worktree/preserved\" ]]", home, checkout)).ExitCode == 0,
                    "Control sourced changed environment or lost synthetic work/logs");
                var discarded = await service.Invoke("discard-prompt", "Ubuntu", CopilotRunService.DiscardPromptScript,
                    [run], token: deadline.Token, environment: env);
                Check(discarded.ExitCode == 0 &&
                    (await Shell("[[ ! -e \"$1/prompt\" && ! -e \"$2/poison-read\" && -f \"$3/worktree/preserved\" ]]",
                        run, home, checkout)).ExitCode == 0,
                    "Private prompt cleanup sourced changed environment or did not preserve synthetic work");
                if (variant == "default")
                {
                    var legacy = env with { XdgConfigHome = null, BgtaskStateDirectory = null };
                    var oldLogs = await service.Invoke("history", "Ubuntu", "\"$1\" logs \"$2\" --tail 2000",
                        [env.Bgtask, name], token: deadline.Token, environment: legacy);
                    Check(oldLogs.ExitCode == 0 && oldLogs.StandardOutput == logs.StandardOutput &&
                        (await Shell("[[ ! -e \"$1/poison-read\" ]]", home)).ExitCode == 0,
                        "Legacy history switched environment after verified task status");
                }
                Console.WriteLine($"REGISTRY variant={variant} stopMs={stopMs:F1} captured=true workAndLogPreserved=true");
            }
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var removed = await native.ExecuteAsync(new("registry-cleanup", "Ubuntu",
                "rm -rf -- \"$1\"; [[ ! -e \"$1\" ]]", [directory]), null, null, cleanup.Token);
            Check(removed.ExitCode == 0, "Exact registry fixture cleanup failed");
        }
    }

    sealed class RegistryTransport(ICopilotRunTransport native, string home) : ICopilotRunTransport
    {
        public Task<CopilotProcessResult> ExecuteAsync(CopilotProcessRequest request, Action<string>? stdout,
            Action<string>? stderr, CancellationToken token) =>
            native.ExecuteAsync(request with
            {
                Script = "export HOME=\"$1\" PATH=/usr/bin:/bin\nunset BASH_ENV ADO_PRVIEW_BASH_ENV XDG_CONFIG_HOME BGTASK_STATE_DIR\nshift\n" +
                    request.Script,
                Arguments = new[] { home }.Concat(request.Arguments).ToArray()
            }, stdout, stderr, token);
    }

    static async Task RunWsl()
    {
        var native = new WslCopilotRunTransport();
        Task<CopilotProcessResult> Shell(string script, params string[] args) =>
            native.ExecuteAsync(new("preflight-fixture", null, script, args), null, null, default);
        var created = await Shell("mktemp -d /tmp/ado-prview-preflight.XXXXXXXXXX");
        Check(created.ExitCode == 0, "Could not create disposable preflight fixture");
        var directory = created.StandardOutput.Trim();
        Check(System.Text.RegularExpressions.Regex.IsMatch(directory, @"\A/tmp/ado-prview-preflight\.[a-zA-Z0-9]+\z"),
            "Invalid fixture directory");
        var home = directory + "/home ' $(never-execute)";
        var checkout = home + "/checkout ' $(never-execute)";
        try
        {
            var setup = await Shell("""
                set -euo pipefail
                home="$1"; checkout="$2"
                mkdir -p -- "$home/.local/bin" "$home/go/bin" "$home/bin" "$checkout"
                git init --quiet -- "$checkout"
                git -C "$checkout" remote add origin https://dev.azure.com/offline-org/offline-project/_git/offline-repo
                printf '#!/bin/bash\ncommand -v fixture-runtime >/dev/null || exit 29\nprintf "%%s\\n" "%s"\n' "$3" > "$home/.local/bin/copilot"
                printf '#!/bin/bash\nexit 0\n' > "$home/.local/bin/fixture-runtime"
                cat > "$home/go/bin/bgtask" <<'SH'
                #!/bin/bash
                set -euo pipefail
                if [[ "$1" == --help ]]; then exit 0; fi
                [[ "$1" == run && "$2" == --name && "$4" == --dir && "$6" == -- ]] || exit 31
                cd -- "$5"
                shift 6
                exec "$@"
                SH
                printf '#!/bin/bash\nprintf "%%s\\n" "%s"\n' "$3" > "$home/bin/copilot"
                chmod +x -- "$home/.local/bin/copilot" "$home/.local/bin/fixture-runtime" "$home/go/bin/bgtask" "$home/bin/copilot"
                """, home, checkout, Help);
            Check(setup.ExitCode == 0, "Could not initialize offline preflight fixture");

            async Task<CopilotProcessResult> Probe(string? path = null, string? cwd = null, string tail = "")
            {
                var script = """
                    export HOME="$1" PATH="$2"
                    unset BASH_ENV ADO_PRVIEW_BASH_ENV COPILOT_HOME
                    shift 2
                    """ + "\n" + CopilotRunService.PreflightDiagnosticsScript + "\n" +
                    CopilotRunService.EnvironmentScript + "\n" + CopilotRunService.PreflightScript + "\n" + tail;
                return await Shell(script, home, path ?? "/usr/bin:/bin", cwd ?? checkout, "offline-repo",
                    CopilotRunService.RunnerScript.Replace("\r\n", "\n"));
            }

            var found = await Probe();
            Check(found.ExitCode == 0, "Per-user CLI preflight failed: " + found.StandardError);
            var fields = found.StandardOutput.Split('\0');
            Check(fields[6] == home + "/.local/bin/copilot" && fields[7] == home + "/go/bin/bgtask" &&
                fields[4] == checkout && fields[8].Contains(Help), "Wrong quoted executable/checkout or bgtask fallback");

            var child = await Probe(tail: """
                setsid bash --noprofile --norc -c '
                  [[ "$(command -v copilot)" == "$1/.local/bin/copilot" ]] || exit 30
                  copilot --help >/dev/null
                ' ado-prview-test-child "$HOME"
                """);
            Check(child.ExitCode == 0, "Runner child did not inherit executable/runtime PATH");

            var launch = await Probe(tail: """
                run="$HOME/run"; mkdir -- "$run"
                printf '%s\n' "$root" offline-branch offline-commit offline-nonce offline-session > "$run/ownership"
                printf 'Offline fake CLI only\n' > "$run/prompt"
                set -- "$bg" offline-task "$root" "$cli" "$run" "$3" offline-session offline-nonce start
                """ + "\n" + CopilotRunService.LaunchScript + "\n" + """
                [[ "$(tail -n 1 -- "$run/outcome")" == 0 ]]
                """);
            Check(launch.ExitCode == 0, "Production launch/runner child failed with pass-through fake supervisor and CLI");

            var priority = await Probe(path: home + "/bin:/usr/bin:/bin");
            Check(priority.ExitCode == 0 && priority.StandardOutput.Split('\0')[6] == home + "/bin/copilot",
                "Per-user fallback replaced existing PATH resolution");

            var configured = await Shell("""
                set -euo pipefail
                mkdir -p -- "$1/.config/bash"
                printf 'export PATH="$HOME/bin:$PATH"\n' > "$1/.config/bash/environment"
                """, home);
            Check(configured.ExitCode == 0, "Could not create fixture environment");
            var environment = await Probe();
            Check(environment.ExitCode == 0 && environment.StandardOutput.Split('\0')[6] == home + "/bin/copilot",
                "Owned environment-file resolution changed");
            Check((await Shell("rm -- \"$1/.config/bash/environment\"", home)).ExitCode == 0, "Fixture cleanup failed");

            var nvm = await Shell("""
                set -euo pipefail
                mkdir -p -- "$1/.nvm"
                printf 'nvm() { export PATH="$HOME/bin:$PATH"; }\n' > "$1/.nvm/nvm.sh"
                """, home);
            Check(nvm.ExitCode == 0, "Could not create fixture nvm");
            var nvmFound = await Probe();
            Check(nvmFound.ExitCode == 0 && nvmFound.StandardOutput.Split('\0')[6] == home + "/bin/copilot",
                "Existing nvm resolution was bypassed");
            Check((await Shell("rm -- \"$1/.nvm/nvm.sh\"", home)).ExitCode == 0, "Fixture cleanup failed");

            var missing = await Probe(cwd: checkout + "/missing");
            Check(missing.ExitCode == 72 && Failure(missing).Contains("directory"), "Missing checkout diagnostic lost");
            Check((await Shell("chmod -x -- \"$1/.local/bin/copilot\"", home)).ExitCode == 0, "Could not disable fixture executable");
            var unavailable = await Probe();
            Check(unavailable.ExitCode == 73 && Failure(unavailable).Contains("~/.local/bin/copilot"),
                "Nonexecutable Copilot was accepted or not diagnosed");
            Check((await Shell("rm -- \"$1/.local/bin/copilot\"", home)).ExitCode == 0, "Could not remove fixture CLI");
            var absent = await Probe();
            Check(absent.ExitCode == 73 && Failure(absent).Contains("~/.local/bin/copilot"), "Missing CLI not diagnosed");
            Check((await Shell("""
                printf '#!/bin/bash\nprintf "secret-token\\n" >&2\nexit 29\n' > "$1/.local/bin/copilot"
                chmod +x -- "$1/.local/bin/copilot"
                """, home)).ExitCode == 0, "Could not create failing fixture CLI");
            var broken = await Probe();
            Check(broken.ExitCode == 29 && broken.StandardError.Contains("secret-token") &&
                Failure(broken).Contains("copilot --help") && !Failure(broken).Contains("secret-token"),
                "CLI execution failure lost stage/exit or leaked raw errors");
            Check((await Shell("cp -- \"$1/bin/copilot\" \"$1/.local/bin/copilot\"; rm -- \"$1/go/bin/bgtask\"", home)).ExitCode == 0,
                "Could not prepare missing-supervisor fixture");
            var noSupervisor = await Probe();
            Check(noSupervisor.ExitCode == 73 && Failure(noSupervisor).Contains("~/go/bin/bgtask"),
                "Missing bgtask fallback was not diagnosed");
            var parent = await Shell("command -v fixture-runtime || true");
            Check(parent.ExitCode == 0 && parent.StandardOutput.Length == 0, "Fixture PATH escaped its child environment");
        }
        finally
        {
            var removed = await Shell("rm -rf -- \"$1\"", directory);
            Check(removed.ExitCode == 0, "Could not remove exact disposable preflight fixture");
        }
    }
}
