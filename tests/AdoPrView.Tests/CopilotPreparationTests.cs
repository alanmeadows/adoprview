using AdoPrView;

static class CopilotPreparationTests
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static string Failure(CopilotProcessResult result)
    {
        try { CopilotRunService.RequirePrepareSuccess(result); }
        catch (Exception ex) { return ex.Message; }
        throw new Exception("Failed preparation was accepted");
    }

    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("Copilot preparation diagnostics preserve exact safe stage exit and reason without raw Git output", () =>
        {
            foreach (var (stage, reason, expected) in new[]
            {
                ("fetch", "authentication", "Sign in to Git"),
                ("fetch", "network", "network"),
                ("worktree", "path", "available space"),
                ("worktree", "conflict", "existing branch"),
                ("worktree", "dependency", "Git LFS"),
                ("worktree", "unsupported", "Linux Git checkout"),
                ("source-commit", "source-moved", "Refresh the PR"),
                ("remote", "remote-changed", "changed after preflight"),
                ("metadata-lock", "lock", "no lock was removed"),
                ("ownership", "path", "ownership receipt")
            })
            {
                var message = Failure(new(73, "SECRET stdout", $"SECRET stderr\nADOPRVIEW_PREPARE:{stage}:{reason}:73\n"));
                Check(message.Contains(expected) && message.Contains("exit 73") && message.Contains("No Copilot task") &&
                    !message.Contains("SECRET"), "Preparation diagnostic lost reason/exit or leaked raw error");
            }
            Check(Failure(new(124, "", "")).Contains("120-second"), "Preparation timeout not distinguished");
            Check(!Failure(new(1, "", "ADOPRVIEW_PREPARE:fetch:authentication:73")).Contains("Sign in"), "Wrong-exit marker trusted");
            return Task.CompletedTask;
        });
        if (Environment.GetEnvironmentVariable("ADOPRVIEW_TEST_WSL_PREPARE") == "1")
            await test("Copilot preparation disposable WSL Git worktrees preserve dirty source and diagnose real setup failures", RunWsl);
    }

    static async Task RunWsl()
    {
        var native = new WslCopilotRunTransport();
        Task<CopilotProcessResult> Shell(string script, params string[] args) =>
            native.ExecuteAsync(new("prepare-fixture", null, script, args), null, null, default);
        var created = await Shell("mktemp -d /tmp/ado-prview-prepare.XXXXXXXXXX");
        var directory = created.StandardOutput.Trim();
        Check(created.ExitCode == 0 && System.Text.RegularExpressions.Regex.IsMatch(directory,
            @"\A/tmp/ado-prview-prepare\.[a-zA-Z0-9]+\z"), "Invalid disposable fixture directory");
        try
        {
            var setup = await Shell("""
                set -euo pipefail
                export HOME="$1/home ' quoted" GIT_CONFIG_NOSYSTEM=1
                unset GIT_DIR GIT_WORK_TREE GIT_INDEX_FILE
                mkdir -p -- "$HOME" "$1/bin"
                git init --quiet --bare "$1/remote.git"
                git init --quiet "$1/source"
                cd -- "$1/source"
                git config user.name "Offline Fixture"; git config user.email "fixture@example.invalid"
                printf 'original\n' > tracked; git add tracked; git commit --quiet -m fixture
                git branch -M topic; git remote add origin "$1/remote.git"
                git push --quiet origin refs/heads/topic
                git worktree add --quiet -b linked "$1/linked ' checkout"
                printf 'user work\n' > tracked; printf 'untracked user work\n' > untracked
                cat > "$1/bin/git" <<'SH'
                #!/bin/bash
                if [[ "${FIXTURE_FAILURE-}" == authentication && " $* " == *" fetch "* ]]; then
                  printf 'fatal: Authentication failed for https://user:SECRET@private.invalid/\n' >&2
                  exit 128
                fi
                if [[ " $* " == *" worktree add "* ]]; then
                  case "${FIXTURE_FAILURE-}" in
                    path) printf 'fatal: cannot create directory: Permission denied\n' >&2; exit 128 ;;
                    unsupported) printf "error: unknown option 'no-track'\n" >&2; exit 129 ;;
                  esac
                fi
                exec /usr/bin/git "$@"
                SH
                chmod +x "$1/bin/git"
                printf '%s\n' "$(git rev-parse HEAD)" "$(git rev-parse --path-format=absolute --git-common-dir)" "$(printf %s "$1/remote.git" | sha256sum | cut -d' ' -f1)"
                """, directory);
            Check(setup.ExitCode == 0, "Could not initialize disposable Git fixture: " + setup.StandardError);
            var fields = setup.StandardOutput.Trim().Split('\n');
            var commit = fields[^3]; var common = fields[^2]; var hash = fields[^1];
            var home = directory + "/home ' quoted";
            async Task<(CopilotProcessResult Result, string Run)> Prepare(string mode, string? exact = null, bool linked = false)
            {
                var id = Guid.NewGuid().ToString("N");
                if (mode == "conflict")
                    Check((await Shell("git -C \"$1/source\" branch \"ado-prview/pr-1-$2\"", directory, id)).ExitCode == 0,
                        "Could not create fixture branch conflict");
                var script = """
                    export HOME="$1/home ' quoted" PATH="$1/bin:/usr/bin:/bin" GIT_CONFIG_NOSYSTEM=1 FIXTURE_FAILURE="$2"
                    unset ADO_PRVIEW_BASH_ENV BASH_ENV GIT_DIR GIT_WORK_TREE GIT_INDEX_FILE
                    shift 2
                    """ + "\n" + CopilotRunService.EnvironmentScript + "\n" + CopilotRunService.PrepareScript;
                var result = await Shell(script, directory, mode, linked ? directory + "/linked ' checkout" : directory + "/source",
                    common, "origin", hash, "refs/heads/topic", exact ?? commit, id, "1", "offline-nonce", "offline-session", "");
                return (result, home + "/.local/state/ado-prview/runs/" + id);
            }
            foreach (var linked in new[] { false, true })
            {
                var (result, run) = await Prepare("success", linked: linked);
                Check(result.ExitCode == 0 && result.StandardOutput == $"ADOPRVIEW1\0WORKTREE\0{run}\0{run}/worktree\0",
                    "Production worktree setup failed for quoted/linked checkout: " + result.StandardError);
                var verified = await Shell("""
                    set -euo pipefail
                    [[ "$(git -C "$1/worktree" rev-parse HEAD)" == "$2" && -f "$1/ownership" ]]
                    [[ "$(stat -c %a "$1/prepare.stderr")" == 600 ]]
                    [[ -z "$(git -C "$1/worktree" status --porcelain)" ]]
                    """, run, commit);
                Check(verified.ExitCode == 0, "Prepared worktree commit/ownership/privacy was incorrect");
            }
            foreach (var (mode, stage, reason) in new[]
            {
                ("authentication", "fetch", "Sign in to Git"),
                ("source-moved", "source commit comparison", "Refresh the PR"),
                ("conflict", "worktree creation", "existing branch"),
                ("path", "worktree creation", "available space"),
                ("unsupported", "worktree creation", "Linux Git checkout")
            })
            {
                var (result, run) = await Prepare(mode, mode == "source-moved" ? new string('1', 40) : null);
                var message = Failure(result);
                Check(message.Contains(stage) && message.Contains(reason) && !message.Contains("SECRET") &&
                    !result.StandardOutput.Contains("WORKTREE"), "Actual shell failure lost stage/reason or claimed an uncreated worktree");
                Check((await Shell("[[ ! -d \"$1/worktree\" && -f \"$1/prepare.stderr\" ]]", run)).ExitCode == 0,
                    "Failed preparation created a worktree or discarded its private error");
                if (mode == "authentication")
                    Check((await Shell("cat -- \"$1/prepare.stderr\"", run)).StandardOutput.Contains("SECRET"),
                        "Original command error was not retained privately");
            }
            Check((await Shell("""
                set -euo pipefail
                printf 'tracked filter=offline\n' > "$1/fixture.attributes"
                git -C "$1/source" config core.attributesFile "$1/fixture.attributes"
                git -C "$1/source" config filter.offline.smudge false
                git -C "$1/source" config filter.offline.required true
                """, directory)).ExitCode == 0, "Could not configure disposable failing checkout filter");
            var filterFailure = await Prepare("checkout-filter");
            Check(filterFailure.Result.ExitCode != 0 && Failure(filterFailure.Result).Contains("Git LFS") &&
                !filterFailure.Result.StandardOutput.Contains("WORKTREE") &&
                (await Shell("[[ ! -d \"$1/worktree\" && ! -f \"$1/ownership\" && -f \"$1/prepare.stderr\" ]]", filterFailure.Run)).ExitCode == 0,
                "Git's rolled-back failed checkout was reported as created or its private error was discarded");
            Check((await Shell("""
                set -euo pipefail
                [[ "$(cat -- "$1/source/tracked")" == "user work" && "$(cat -- "$1/source/untracked")" == "untracked user work" ]]
                [[ "$(git -C "$1/source" rev-parse HEAD)" == "$2" && "$(git -C "$1/source" symbolic-ref --short HEAD)" == topic ]]
                """, directory, commit)).ExitCode == 0, "Preparation changed existing source work");
        }
        finally
        {
            // Every checkout, branch and private error here belongs to this exact disposable fixture.
            Check((await Shell("rm -rf -- \"$1\"", directory)).ExitCode == 0, "Could not remove disposable Git fixture");
        }
    }
}
