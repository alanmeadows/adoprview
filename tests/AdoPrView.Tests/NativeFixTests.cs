using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AdoPrView;

internal static class NativeFixTests
{
    const string Help = "--no-auto-update --no-ask-user --stream --no-color --deny-tool --yolo --session-id --resume";
    static string Exe => Path.Combine(AppContext.BaseDirectory, "AdoPrView.Tests.exe");
    sealed record ShellProbe(bool Launched, int Pid, long Started, int Error);

    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void DispatchSyntheticFixture()
    {
        if (Fixture(Environment.GetCommandLineArgs()[1..]))
            Environment.Exit(Environment.ExitCode);
    }

    static bool Fixture(string[] args)
    {
        if (args is ["--native-fix-shell-launch", var ownership, var outcome])
        {
            using var self = Process.GetCurrentProcess();
            try
            {
                NativeFixSupervisor.Launch(NativeFixSupervisor.Read<NativeFixSupervisor.Specification>(ownership));
                NativeFixSupervisor.Write(outcome, new ShellProbe(true, self.Id, self.StartTime.ToUniversalTime().Ticks, 0));
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                NativeFixSupervisor.Write(outcome, new ShellProbe(false, self.Id, self.StartTime.ToUniversalTime().Ticks, ex.NativeErrorCode));
            }
            return true;
        }
        if (args is ["--native-fix-child", var file])
        {
            File.WriteAllText(file, Environment.ProcessId.ToString());
            Console.WriteLine("synthetic descendant ready");
            Thread.Sleep(Timeout.Infinite);
            return true;
        }
        if (args is ["--no-auto-update", "--help"])
        {
            Console.WriteLine(Help);
            return true;
        }
        if (args.Contains("--session-id") && File.Exists("native-fixture.marker"))
        {
            using var input = Console.OpenStandardInput();
            using var reader = new StreamReader(input, Encoding.UTF8);
            var prompt = reader.ReadToEnd();
            File.WriteAllText("fixture-parent.pid", Environment.ProcessId.ToString());
            File.WriteAllText("fixture-prompt.txt", prompt);
            Console.WriteLine("synthetic native Fix started");
            Console.Error.WriteLine("synthetic stderr evidence");
            if (File.ReadAllText("native-fixture.marker") == "new-start")
            {
                File.WriteAllText("first-start-baseline.txt", File.ReadAllText("sample.txt"));
                File.WriteAllText("native-fix-edit.txt", "synthetic owned fix edit");
                if (prompt.Contains("synthetic-auth-failure"))
                {
                    Console.Error.WriteLine("Error: You are not logged in. Run copilot login.");
                    Environment.ExitCode = 1;
                    return true;
                }
                if (prompt.Contains("synthetic-complete")) return true;
            }
            if (File.ReadAllText("native-fixture.marker") == "cleanup-failure")
            {
                var privatePrompt = Path.Combine(Directory.GetParent(Environment.CurrentDirectory)!.FullName, "prompt");
                File.Delete(privatePrompt);
                Directory.CreateDirectory(privatePrompt);
                File.WriteAllText(Path.Combine(privatePrompt, "user-edit.txt"), "preserve cleanup obstacle");
                return true;
            }
            if (File.ReadAllText("native-fixture.marker") == "receipt-failure")
            {
                Directory.CreateDirectory(Path.Combine(Directory.GetParent(Environment.CurrentDirectory)!.FullName, "receipt.json.pending"));
                return true;
            }
            if (File.ReadAllText("native-fixture.marker") == "auth")
            {
                Console.Error.WriteLine("Error: You are not logged in. Run copilot login.");
                Environment.ExitCode = 1;
                return true;
            }
            if (File.ReadAllText("native-fixture.marker") == "complete")
                return true;
            var start = new ProcessStartInfo(Exe) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--native-fix-child");
            start.ArgumentList.Add(Path.GetFullPath("fixture-child.pid"));
            using var child = Process.Start(start)!;
            Thread.Sleep(Timeout.Infinite);
            return true;
        }
        return false;
    }

    static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    static async Task Until(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        while (!predicate()) await Task.Delay(50, deadline.Token);
    }

    static bool Alive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
    }

    static NativeFixSupervisor.Specification Spec(string directory, bool push = false)
    {
        var id = Guid.NewGuid().ToString("N");
        var run = Path.Combine(directory, "native", id);
        Directory.CreateDirectory(Path.Combine(run, "worktree"));
        var spec = new NativeFixSupervisor.Specification(id, new string('b', 64), Guid.NewGuid().ToString(), run,
            directory, directory, Path.Combine(run, "worktree"), "ado-prview/pr-1-" + id, new string('1', 40),
            Path.Combine(directory, "nonexistent-git.exe"), Exe, "origin", "https://dev.azure.com/offline-org/offline-project/_git/offline-repo",
            ["https://dev.azure.com/offline-org/offline-project/_git/offline-repo"], push);
        NativeFixSupervisor.Write(Path.Combine(run, "ownership.json"), spec);
        File.WriteAllText(Path.Combine(run, "prompt"), "synthetic prompt only", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(run, "worktree", "native-fixture.marker"), "hang");
        return spec;
    }

    static CopilotRunRequest Request(NativeFixSupervisor.Specification spec) => new("offline:1", 1, "Synthetic native Fix",
        "offline-org", "offline-project", "offline-repo", "offline", "refs/heads/source", spec.Commit, "",
        Directory: spec.SourceDirectory, PushToSource: spec.PushToSource);

    static void Save(string directory, NativeFixSupervisor.Specification spec)
    {
        var state = new CopilotRunState(spec.RunId, Request(spec), "running", "", null, null, null,
            spec.SourceDirectory, spec.Worktree, "in-use", DateTimeOffset.UtcNow, null)
        {
            Backend = "windows-native", SessionId = spec.SessionId, MayBeRunning = true,
            LogPath = Path.Combine(spec.RunDirectory, "stdout.log")
        };
        var record = new CopilotRunService.SavedRun(1, state, null, spec.RunDirectory, spec.Token, true, false, null)
        { Native = spec };
        File.WriteAllText(Path.Combine(directory, spec.RunId + ".json"), JsonSerializer.Serialize(record));
    }

    static async Task Clean(NativeFixSupervisor.Specification spec)
    {
        var receipt = NativeFixSupervisor.Status(spec);
        if (receipt is not null && NativeFixSupervisor.SupervisorAlive(receipt) && !receipt.DescendantsStopped)
        {
            NativeFixSupervisor.RequestCancel(spec);
            await Until(() => NativeFixSupervisor.Status(spec)?.DescendantsStopped == true);
        }
        if (NativeFixSupervisor.Status(spec) is { } owner) await Until(() => !NativeFixSupervisor.SupervisorAlive(owner));
    }

    internal static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await NativeFixStartTests.Run(test);
        if (Environment.GetEnvironmentVariable("ADOPRVIEW_TEST_NATIVE_SHELL_PROBE") == "1")
            await test("Native Fix permitted desktop-shell fixture checks production launcher exit independence", async () =>
            {
                var root = Path.Combine(Environment.CurrentDirectory, ".native-shell-fixture-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                var spec = Spec(root);
                try
                {
                    using var current = Process.GetCurrentProcess();
                    var desktops = Process.GetProcessesByName("explorer");
                    try
                    {
                        if (!desktops.Any(process => process.SessionId == current.SessionId))
                        {
                            Console.WriteLine("NATIVE-SHELL production detach not established: no existing desktop shell in this session; no shell was started");
                            return;
                        }
                    }
                    finally { foreach (var desktop in desktops) desktop.Dispose(); }
                    var deployment = NativeFixDeployment.Ensure(AppContext.BaseDirectory, root);
                    spec = spec with { SupervisorExecutable = deployment.Executable, SupervisorBundleId = deployment.BundleId };
                    NativeFixSupervisor.Write(NativeFixSupervisor.FilePath(spec, "ownership.json"), spec);
                    var outcome = Path.Combine(root, "shell-outcome.json");
                    var arguments = "--native-fix-shell-launch " + NativeFixSupervisor.Quote(NativeFixSupervisor.FilePath(spec, "ownership.json")) +
                        " " + NativeFixSupervisor.Quote(outcome);
                    var script = Path.Combine(root, "shell-launch.ps1");
                    static string Ps(string value) => "'" + value.Replace("'", "''") + "'";
                    File.WriteAllText(script, "$ErrorActionPreference='Stop'\n$shell=New-Object -ComObject Shell.Application\n" +
                        "$shell.ShellExecute(" + Ps(Exe) + "," + Ps(arguments) + "," + Ps(root) + ",'open',0)\n" +
                        "[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)\n");
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var shell = await CopilotRunService.NativeCommandAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                        "System32", "WindowsPowerShell", "v1.0", "powershell.exe"), root,
                        ["-NoLogo", "-NonInteractive", "-NoProfile", "-File", script], deadline.Token);
                    if (shell.ExitCode != 0)
                    {
                        Console.WriteLine("NATIVE-SHELL production detach not established: desktop shell COM launch unavailable; no global/task/account configuration was changed");
                        return;
                    }
                    await Until(() => File.Exists(outcome));
                    var result = NativeFixSupervisor.Read<ShellProbe>(outcome);
                    var launcher = new NativeFixSupervisor.Receipt(spec.RunId, spec.Token, spec.SessionId, result.Pid, result.Started, null, null, false);
                    await Until(() => !NativeFixSupervisor.SupervisorAlive(launcher));
                    if (!result.Launched)
                    {
                        Console.WriteLine("NATIVE-SHELL production detach not established: permitted shell host refused breakaway (Windows error " + result.Error + ")");
                        return;
                    }
                    await Until(() => File.Exists(Path.Combine(spec.Worktree, "fixture-child.pid")));
                    Check(NativeFixSupervisor.ActiveProcesses(spec) >= 2 &&
                        NativeFixSupervisor.SupervisorAlive(NativeFixSupervisor.Status(spec)!), "Production job did not survive its actual launcher process exiting");
                    NativeFixSupervisor.RequestCancel(spec);
                    await Until(() => NativeFixSupervisor.Status(spec) is { DescendantsStopped: true });
                    Check(NativeFixSupervisor.ActiveProcesses(spec) is null or 0, "Production detached run descendants were not stopped exactly");
                    Console.WriteLine("NATIVE-SHELL production breakaway succeeded outside restricted tool parent; actual launcher exited; owned supervisor/CLI/descendant survived and exact Stop confirmed");
                }
                finally { await Clean(spec); Directory.Delete(root, true); }
            });
        await test("Native Fix accepts public organization-only ADO clone usernames but never copies embedded credentials", () =>
        {
            Check(!CopilotRunService.NativeRemoteHasCredential("https://offline-org@dev.azure.com/offline-org/project/_git/repo", "offline-org") &&
                !CopilotRunService.NativeRemoteHasCredential("https://dev.azure.com/offline-org/project/_git/repo", "offline-org"),
                "Standard ADO clone URL's public organization username required a Git configuration rewrite");
            Check(CopilotRunService.NativeRemoteHasCredential("https://offline-org:synthetic-secret@dev.azure.com/offline-org/project/_git/repo", "offline-org") &&
                CopilotRunService.NativeRemoteHasCredential("https://synthetic-secret@dev.azure.com/offline-org/project/_git/repo", "offline-org") &&
                CopilotRunService.NativeRemoteHasCredential("ssh://git:synthetic-secret@ssh.dev.azure.com/v3/offline-org/project/repo", "offline-org"),
                "Native durable remote metadata could copy embedded authentication secrets");
            return Task.CompletedTask;
        });
        await test("Native Fix npm shim resolves only verified bounded native package entrypoints and skips malformed PATH", () =>
        {
            var root = Path.Combine(Environment.CurrentDirectory, ".native-npm-fixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var loader = Path.Combine(root, "node_modules", "@github", "copilot");
            var platform = Path.Combine(root, "node_modules", "@github", "copilot-win32-x64");
            Directory.CreateDirectory(loader); Directory.CreateDirectory(platform);
            try
            {
                File.WriteAllText(Path.Combine(root, "copilot.cmd"), "@echo off\r\n\"%dp0%\\node_modules\\@github\\copilot\\npm-loader.js\" %*\r\n");
                File.WriteAllText(Path.Combine(loader, "package.json"),
                    """{"name":"@github/copilot","version":"1.0.112","bin":{"copilot":"npm-loader.js"},"optionalDependencies":{"@github/copilot-win32-x64":"1.0.112"}}""");
                File.WriteAllText(Path.Combine(platform, "package.json"),
                    """{"name":"@github/copilot-win32-x64","version":"1.0.112","os":["win32"],"cpu":["x64"],"exports":{".":{"import":"./copilot.exe"}}}""");
                File.Copy(Exe, Path.Combine(platform, "copilot.exe"));
                Check(CopilotRunService.NativeExecutable("copilot", searchPath: "\0malformed;" + root) == Path.Combine(platform, "copilot.exe"),
                    "Official npm shim was rejected or executed rather than resolving its native payload");
                Check(CopilotRunService.NativeExecutable("copilot", Path.Combine(root, "copilot.cmd")) == Path.Combine(platform, "copilot.exe"),
                    "Explicit npm shim did not resolve directly to the native executable");
                var nested = Path.Combine(loader, "node_modules", "@github", "copilot-win32-x64");
                Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
                Directory.Move(platform, nested);
                platform = nested;
                File.WriteAllText(Path.Combine(platform, "package.json"),
                    """{"name":"@github/copilot-win32-x64","version":"1.0.112","os":["win32"],"cpu":["x64"],"main":"copilot.exe"}""");
                Check(NativeCopilotExecutable.ResolveNpmShim(Path.Combine(root, "copilot.cmd"), out _) == Path.Combine(platform, "copilot.exe"),
                    "Nested npm native package main entrypoint was not supported");
                File.WriteAllText(Path.Combine(platform, "package.json"),
                    """{"name":"@github/copilot-win32-x64","version":"1.0.111","os":["win32"],"cpu":["x64"],"main":"copilot.exe"}""");
                Check(NativeCopilotExecutable.ResolveNpmShim(Path.Combine(root, "copilot.cmd"), out _) is null,
                    "Mismatched npm loader/platform package versions were accepted");
                File.WriteAllText(Path.Combine(platform, "package.json"),
                    """{"name":"@github/copilot-win32-x64","version":"1.0.112","os":["win32"],"cpu":["x64"],"main":"../../../arbitrary.exe"}""");
                Check(NativeCopilotExecutable.ResolveNpmShim(Path.Combine(root, "copilot.cmd"), out var warning) is null &&
                    warning!.Contains("could not be verified"), "Npm entrypoint traversal was trusted");
                File.WriteAllText(Path.Combine(platform, "package.json"),
                    """{"name":"@github/copilot-win32-x64","version":"1.0.112","os":["win32"],"cpu":["x64"],"main":"missing.exe"}""");
                Check(NativeCopilotExecutable.ResolveNpmShim(Path.Combine(root, "copilot.cmd"), out warning) is null &&
                    warning!.Contains("no matching native platform executable"), "Missing npm executable lacked specific repair guidance");
                File.WriteAllText(Path.Combine(root, "copilot.cmd"), "@echo off\r\necho unrelated shell shim\r\n");
                Check(NativeCopilotExecutable.ResolveNpmShim(Path.Combine(root, "copilot.cmd"), out _) is null,
                    "An arbitrary shell shim was accepted as a verified Copilot npm installation");
                try { CopilotRunService.NativeExecutable("git", searchPath: "\0malformed;relative-folder"); throw new Exception("Malformed PATH accepted"); }
                catch (Exception ex) when (ex.Message.Contains("Malformed or relative PATH")) { }
            }
            finally { Directory.Delete(root, true); }
            return Task.CompletedTask;
        });

        await test("Native Fix prompt and terminal receipt failures remain visible without false cleanup or replay", async () =>
        {
            var root = Path.Combine(Environment.CurrentDirectory, ".native-fix-fixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var cleanup = Spec(root);
            var receipt = Spec(root);
            try
            {
                File.WriteAllText(Path.Combine(cleanup.Worktree, "native-fixture.marker"), "cleanup-failure");
                NativeFixSupervisor.Launch(cleanup, nestedSyntheticFixture: true);
                await Until(() => NativeFixSupervisor.Status(cleanup) is { ExitCode: 0, DescendantsStopped: true });
                Check(NativeFixSupervisor.Status(cleanup)!.Error!.Contains("Private prompt cleanup could not be confirmed") &&
                    File.Exists(Path.Combine(cleanup.RunDirectory, "prompt", "user-edit.txt")), "Prompt cleanup failure was hidden or user work deleted");
                File.WriteAllText(Path.Combine(receipt.Worktree, "native-fixture.marker"), "receipt-failure");
                NativeFixSupervisor.Launch(receipt, nestedSyntheticFixture: true);
                await Until(() => NativeFixSupervisor.Status(receipt) is { } status && !NativeFixSupervisor.SupervisorAlive(status));
                Save(root, cleanup); Save(root, receipt);
                await using var service = new CopilotRunService(stateDirectory: root);
                await service.RecoverAsync();
                await Until(() => service.Get(receipt.RunId)!.Status == "needs-attention");
                Check(service.Get(cleanup.RunId)!.Error!.Contains("Private prompt cleanup could not be confirmed"),
                    "Dashboard recovery discarded the terminal cleanup diagnostic");
                Check(service.Get(receipt.RunId) is { MayBeRunning: false, ResumeAttempts: 0 } failed &&
                    failed.Output.Contains("Native exit receipt could not be persisted"), "Receipt persistence failure was silently dropped or replayed");
            }
            finally { await Clean(cleanup); await Clean(receipt); Directory.Delete(root, true); }
        });
        await test("Native Fix prerequisites fail without user PATH and old JSON remains legacy", () =>
        {
            try { CopilotRunService.NativeExecutable("copilot", searchPath: ""); throw new Exception("Missing native CLI was accepted"); }
            catch (Exception ex) when (ex.Message.Contains("not found on PATH")) { }
            try { CopilotRunService.NativeExecutable("copilot", Path.Combine(Environment.CurrentDirectory, "absent.exe")); throw new Exception("Missing override was accepted"); }
            catch (Exception ex) when (ex.Message.Contains("configured native copilot executable")) { }
            var request = new CopilotRunRequest("offline:1", 1, "fixture", "org", "project", "repo", "repo-id",
                "refs/heads/source", new string('1', 40), "fixture", Directory: Environment.CurrentDirectory);
            var state = new CopilotRunState(new string('a', 32), request, "failed", "", 1, null, "Ubuntu", "/root", "/work",
                "retained", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) { SessionId = Guid.NewGuid().ToString() };
            var oldJson = JsonSerializer.SerializeToNode(state)!;
            oldJson.AsObject().Remove("Backend");
            Check(JsonSerializer.Deserialize<CopilotRunState>(oldJson.ToJsonString())!.Backend == "legacy-wsl",
                "Old WSL state was silently classified as a new Windows run");
            return Task.CompletedTask;
        });

        await test("Native Fix new runs reject missing Windows tools and SDK wrappers without WSL or user PATH", async () =>
        {
            var directory = Path.Combine(Environment.CurrentDirectory, ".native-fix-fixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var oldPath = Environment.GetEnvironmentVariable("PATH");
            try
            {
                Environment.SetEnvironmentVariable("PATH", directory);
                var request = new CopilotRunRequest("offline:2", 2, "Synthetic prerequisites", "org", "project", "repo", "repo-id",
                    "refs/heads/source", new string('1', 40), "synthetic prompt", Directory: directory);
                await using (var service = new CopilotRunService(stateDirectory: Path.Combine(directory, "missing-git")))
                {
                    var run = service.Start(request);
                    await Until(() => service.Get(run.RunId)!.Status == "failed");
                    Check(service.Get(run.RunId) is { Backend: "windows-native", WorktreePath: null, MayBeRunning: false } state &&
                        state.Error!.Contains("git.exe was not found on PATH"), "New native run tried WSL or missed its exact Git prerequisite");
                }
                File.Copy(Exe, Path.Combine(directory, "git.exe"));
                await using (var service = new CopilotRunService(stateDirectory: Path.Combine(directory, "missing-cli")))
                {
                    var run = service.Start(request with { NativeCopilotPath = Path.Combine(directory, "absent.exe") });
                    await Until(() => service.Get(run.RunId)!.Status == "failed");
                    Check(service.Get(run.RunId)!.Error!.Contains("configured native copilot executable") &&
                        service.Get(run.RunId)!.WorktreePath is null, "Missing native CLI override was not diagnosed before Git/repository IO");
                }
                var wrapper = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "copilot-runtime.exe");
                await using (var service = new CopilotRunService(stateDirectory: Path.Combine(directory, "sdk-wrapper")))
                {
                    var run = service.Start(request with { NativeCopilotPath = wrapper });
                    await Until(() => service.Get(run.RunId)!.Status == "failed");
                    Check(service.Get(run.RunId)!.Error!.Contains("not a compatible full CLI") &&
                        service.Get(run.RunId)!.WorktreePath is null, "SDK-only runtime was falsely accepted as a native prompt CLI");
                }
            }
            finally { Environment.SetEnvironmentVariable("PATH", oldPath); Directory.Delete(directory, true); }
        });

        await test("Native Fix production detach launch fails safely when the host prohibits job breakaway", async () =>
        {
            var directory = Path.Combine(Environment.CurrentDirectory, ".native-fix-fixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var spec = Spec(directory);
            try
            {
                try
                {
                    NativeFixSupervisor.Launch(spec);
                    await Until(() => File.Exists(Path.Combine(spec.Worktree, "fixture-child.pid")));
                    Check(NativeFixSupervisor.ActiveProcesses(spec) >= 2, "Independent native launch lost job supervision");
                    Console.WriteLine("NATIVE-DETACH independent launch available in this host");
                }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5)
                {
                    Check(NativeFixSupervisor.Status(spec) is null && NativeFixSupervisor.ActiveProcesses(spec) is null &&
                        File.Exists(Path.Combine(spec.RunDirectory, "prompt")), "Denied detach launch consumed a prompt or left a native child running");
                    Console.WriteLine("NATIVE-DETACH independent launch blocked by this host job (Windows error 5); nested fixtures do not establish host-exit independence");
                }
            }
            finally { await Clean(spec); Directory.Delete(directory, true); }
        });

        await test("Native Fix process job owns descendants, survives dashboard detach, and cancels only exact run", async () =>
        {
            var directory = Path.Combine(Environment.CurrentDirectory, ".native-fix-fixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var spec = Spec(directory);
            var other = Spec(directory);
            try
            {
                NativeFixSupervisor.Launch(spec, nestedSyntheticFixture: true);
                NativeFixSupervisor.Launch(other, nestedSyntheticFixture: true);
                await Until(() => File.Exists(Path.Combine(spec.Worktree, "fixture-child.pid")) &&
                    File.Exists(Path.Combine(other.Worktree, "fixture-child.pid")));
                var parentPid = int.Parse(File.ReadAllText(Path.Combine(spec.Worktree, "fixture-parent.pid")));
                var childPid = int.Parse(File.ReadAllText(Path.Combine(spec.Worktree, "fixture-child.pid")));
                var otherPid = int.Parse(File.ReadAllText(Path.Combine(other.Worktree, "fixture-parent.pid")));
                Check(NativeFixSupervisor.ActiveProcesses(spec) >= 2, "Owned native job did not include its descendant");
                Check(File.ReadAllText(Path.Combine(spec.Worktree, "fixture-prompt.txt")) == "synthetic prompt only", "Native prompt stdin changed");
                Save(directory, spec);
                await using (var first = new CopilotRunService(stateDirectory: directory))
                {
                    await first.RecoverAsync();
                    await Until(() => first.Get(spec.RunId)!.Output.Contains("synthetic native Fix"));
                    Check(first.Get(spec.RunId)!.Backend == "windows-native", "Native recovery was routed through WSL");
                }
                Check(Alive(parentPid) && Alive(childPid), "Dashboard disposal cancelled a detached native run");
                await using (var reopened = new CopilotRunService(stateDirectory: directory))
                {
                    await reopened.RecoverAsync();
                    var stopped = await reopened.CancelAsync(spec.RunId);
                    Check(stopped is { Status: "cancelled", MayBeRunning: false }, "Exact native Stop was not confirmed: " + stopped?.Message);
                    Check(stopped!.Output.Contains("synthetic stderr evidence"), "Recovered native stderr history was lost");
                }
                await Until(() => !Alive(parentPid) && !Alive(childPid));
                Check(NativeFixSupervisor.ActiveProcesses(spec) is null or 0, "Native Stop left owned descendants active");
                Check(Alive(otherPid), "Exact-run cancellation killed an unrelated native Fix run");
                Check(Directory.Exists(spec.Worktree) && File.Exists(Path.Combine(spec.Worktree, "fixture-prompt.txt")), "Native cancellation discarded user work");
                await using var final = new CopilotRunService(stateDirectory: directory);
                await final.RecoverAsync();
                Check(final.Get(spec.RunId) is { Status: "cancelled", MayBeRunning: false }, "Terminal native cancellation became a fresh/resumed run");
            }
            finally
            {
                await Clean(spec); await Clean(other);
                Directory.Delete(directory, true);
            }
        });

        await test("Native Fix receipt tampering refuses cancellation and native prompt completion is not replayed", async () =>
        {
            var directory = Path.Combine(Environment.CurrentDirectory, ".native-fix-fixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var spec = Spec(directory, push: true);
            try
            {
                var args = NativeFixSupervisor.Arguments(spec);
                Check(!args.Contains("shell(git push)") && args.Contains("shell(gh pr merge)") &&
                    args.Contains("shell(az repos pr update)") && args.Contains("--yolo"), "Native permissions/publication denies diverged");
                var instructions = CopilotRunService.NativePublishInstructions(Request(spec), spec);
                Check(instructions.Contains("HEAD:refs/heads/source") && instructions.Contains("--no-follow-tags") &&
                    instructions.Contains("--recurse-submodules=check") && instructions.Contains("no force"), "Native publish gates were lost");
                var bad = spec with { Token = new string('c', 64) };
                try { NativeFixSupervisor.RequestCancel(bad); throw new Exception("Tampered owner accepted"); }
                catch (InvalidOperationException ex) when (ex.Message.Contains("receipt changed")) { }
                Check(!File.Exists(Path.Combine(spec.RunDirectory, "cancel.json")), "Mismatched ownership wrote Stop");
                File.WriteAllText(Path.Combine(spec.Worktree, "native-fixture.marker"), "complete");
                NativeFixSupervisor.Launch(spec, nestedSyntheticFixture: true);
                await Until(() => NativeFixSupervisor.Status(spec) is { ExitCode: 0, DescendantsStopped: true });
                await Clean(spec);
                Save(directory, spec);
                await using var service = new CopilotRunService(stateDirectory: directory);
                await service.RecoverAsync();
                await Until(() => service.Get(spec.RunId)!.Status == "completed");
                Check(service.Get(spec.RunId) is { MayBeRunning: false, ResumeAttempts: 0, WorktreeRetained: true }, "Native recovery replayed/removed completed user work");
                Check(!File.Exists(Path.Combine(spec.RunDirectory, "prompt")), "Consumed private native prompt was retained");
                NativeFixSupervisor.Launch(spec, nestedSyntheticFixture: true);
                await Task.Delay(250);
                Check(NativeFixSupervisor.Status(spec) is { ExitCode: 0, DescendantsStopped: true }, "Duplicate launch replayed an existing native session");
            }
            finally { await Clean(spec); Directory.Delete(directory, true); }
        });

        await test("Native Fix supervisor crash kills owned descendants and recovery preserves edits without replay", async () =>
        {
            var directory = Path.Combine(Environment.CurrentDirectory, ".native-fix-fixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var spec = Spec(directory);
            try
            {
                NativeFixSupervisor.Launch(spec, nestedSyntheticFixture: true);
                await Until(() => File.Exists(Path.Combine(spec.Worktree, "fixture-child.pid")));
                var receipt = NativeFixSupervisor.Status(spec)!;
                var child = int.Parse(File.ReadAllText(Path.Combine(spec.Worktree, "fixture-child.pid")));
                File.WriteAllText(Path.Combine(spec.Worktree, "user-edit.txt"), "preserve this synthetic edit");
                using (var supervisor = Process.GetProcessById(receipt.SupervisorPid))
                {
                    Check(supervisor.StartTime.ToUniversalTime().Ticks == receipt.SupervisorStartedTicks,
                        "Synthetic supervisor PID was reused");
                    supervisor.Kill();
                    await supervisor.WaitForExitAsync();
                }
                await Until(() => !Alive(child) && NativeFixSupervisor.ActiveProcesses(spec) is null or 0);
                Save(directory, spec);
                await using var service = new CopilotRunService(stateDirectory: directory);
                await service.RecoverAsync();
                await Until(() => service.Get(spec.RunId)!.Status == "needs-attention");
                Check(service.Get(spec.RunId) is { MayBeRunning: false, ResumeAttempts: 0 },
                    "Native crash recovery fabricated a live/resumed prompt");
                var cancelled = await service.CancelAsync(spec.RunId);
                Check(cancelled is { Status: "cancelled", MayBeRunning: false }, "A proven stopped native crash could not be explicitly reconciled");
                Check(File.ReadAllText(Path.Combine(spec.Worktree, "user-edit.txt")) == "preserve this synthetic edit",
                    "Native crash reconciliation discarded user edits");
            }
            finally { await Clean(spec); Directory.Delete(directory, true); }
        });

        await test("Native Fix durable cancellation works while dashboard is closed and auth guidance belongs to chosen CLI", async () =>
        {
            var directory = Path.Combine(Environment.CurrentDirectory, ".native-fix-fixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var spec = Spec(directory);
            var auth = Spec(directory);
            try
            {
                NativeFixSupervisor.Launch(spec, nestedSyntheticFixture: true);
                await Until(() => File.Exists(Path.Combine(spec.Worktree, "fixture-child.pid")));
                Save(directory, spec);
                File.WriteAllText(Path.Combine(directory, spec.RunId + ".cancelled"), "");
                try { await Until(() => NativeFixSupervisor.Status(spec) is { ExitCode: 143, DescendantsStopped: true }); }
                catch (Exception ex)
                {
                    throw new Exception("Synthetic durable cancellation receipt=" + JsonSerializer.Serialize(NativeFixSupervisor.Status(spec)) +
                        "; active=" + NativeFixSupervisor.ActiveProcesses(spec) + "; supervisor stderr=" +
                        File.ReadAllText(NativeFixSupervisor.FilePath(spec, "supervisor.stderr.log")), ex);
                }
                File.WriteAllText(Path.Combine(auth.Worktree, "native-fixture.marker"), "auth");
                NativeFixSupervisor.Launch(auth, nestedSyntheticFixture: true);
                await Until(() => NativeFixSupervisor.Status(auth) is { ExitCode: 1, DescendantsStopped: true });
                Save(directory, auth);
                await using var service = new CopilotRunService(stateDirectory: directory);
                await service.RecoverAsync();
                await Until(() => service.Get(auth.RunId)!.Status == "failed");
                Check(service.Get(spec.RunId) is { Status: "cancelled", MayBeRunning: false },
                    "A durable exact-run cancellation was lost while no dashboard was watching");
                Check(service.Get(auth.RunId)!.Error!.Contains("CLI's own 'copilot login'") &&
                    !service.Get(auth.RunId)!.Error!.Contains("gh auth login"), "Native Fix auth guidance used another host/account");
            }
            finally { await Clean(spec); await Clean(auth); Directory.Delete(directory, true); }
        });
    }
}
