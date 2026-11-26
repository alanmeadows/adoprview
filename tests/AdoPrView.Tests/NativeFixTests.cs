using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AdoPrView;

internal static class NativeFixTests
{
    const string Help = "--no-auto-update --no-ask-user --stream --no-color --deny-tool --yolo --session-id --resume";
    static string Exe => Path.Combine(AppContext.BaseDirectory, "AdoPrView.Tests.exe");

    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void DispatchSyntheticFixture()
    {
        if (Fixture(Environment.GetCommandLineArgs()[1..]))
            Environment.Exit(Environment.ExitCode);
    }

    static bool Fixture(string[] args)
    {
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
                await Until(() => NativeFixSupervisor.Status(spec) is { ExitCode: 143, DescendantsStopped: true });
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
