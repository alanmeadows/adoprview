using AdoPrView;

internal static class NativeFixStartTests
{
    static string Exe => Path.Combine(AppContext.BaseDirectory, "AdoPrView.Tests.exe");
    static string Git => CopilotRunService.NativeExecutable("git",
        Environment.GetEnvironmentVariable("ADOPRVIEW_TEST_NATIVE_GIT") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", "git.exe"));
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!condition()) await Task.Delay(50, deadline.Token);
    }

    static async Task<string> Command(string directory, params string[] arguments)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await CopilotRunService.NativeCommandAsync(Git, directory,
            ["--no-replace-objects", "-c", "core.fsmonitor=false", "-c", "core.hooksPath=NUL",
             "-c", "init.templateDir=", "-c", "commit.gpgSign=false",
             "-c", "user.name=Native Fixture", "-c", "user.email=native-fixture@example.invalid", .. arguments], deadline.Token);
        Check(result.ExitCode == 0, "Local synthetic Git fixture command failed: " + arguments[0]);
        return result.StandardOutput.TrimEnd('\r', '\n');
    }

    sealed class LocalSource : IAsyncDisposable
    {
        public string DirectoryPath = Path.Combine(Environment.CurrentDirectory, ".native-start-fixture-" + Guid.NewGuid().ToString("N"));
        public string Root => Path.Combine(DirectoryPath, "source with spaces");
        public string Upstream => Path.Combine(DirectoryPath, "upstream.git");
        public string Other => Path.Combine(DirectoryPath, "other.git");
        public string State => Path.Combine(DirectoryPath, "state");
        public string Monitor => Path.Combine(DirectoryPath, "fsmonitor-invoked");
        public string Hook => Path.Combine(DirectoryPath, "post-checkout-invoked");
        public string Commit = "", Poison = "", BeforeStatus = "", BeforeRemote = "", BeforeConfig = "";
        readonly string? oldPath = Environment.GetEnvironmentVariable("PATH");

        public static async Task<LocalSource> Create()
        {
            var fixture = new LocalSource();
            Directory.CreateDirectory(fixture.DirectoryPath);
            Environment.SetEnvironmentVariable("PATH", Path.GetDirectoryName(Git));
            var seed = Path.Combine(fixture.DirectoryPath, "seed");
            Directory.CreateDirectory(seed);
            await Command(seed, "init", "--initial-branch=source");
            File.WriteAllText(Path.Combine(seed, "sample.txt"), "captured baseline");
            File.WriteAllText(Path.Combine(seed, "native-fixture.marker"), "new-start");
            await Command(seed, "add", ".");
            await Command(seed, "commit", "-m", "synthetic baseline");
            fixture.Commit = await Command(seed, "rev-parse", "HEAD");
            await Command(fixture.DirectoryPath, "init", "--bare", fixture.Upstream);
            await Command(fixture.DirectoryPath, "init", "--bare", fixture.Other);
            await Command(seed, "push", fixture.Upstream, "HEAD:refs/heads/source");
            await Command(fixture.DirectoryPath, "clone", "--local", "--branch", "source", fixture.Upstream, fixture.Root);
            File.WriteAllText(Path.Combine(seed, "sample.txt"), "replacement poison");
            await Command(seed, "add", ".");
            await Command(seed, "commit", "-m", "synthetic replacement poison");
            fixture.Poison = await Command(seed, "rev-parse", "HEAD");
            await Command(fixture.Root, "fetch", "--no-write-fetch-head", seed, "refs/heads/source");
            await Command(fixture.Root, "replace", fixture.Commit, fixture.Poison);
            var monitorScript = Path.Combine(fixture.DirectoryPath, "fsmonitor.cmd");
            File.WriteAllText(monitorScript, "@echo off\r\necho invoked > \"" + fixture.Monitor + "\"\r\n");
            await Command(fixture.Root, "config", "core.fsmonitor", "\"" + monitorScript + "\"");
            Directory.CreateDirectory(Path.Combine(fixture.Root, ".git", "hooks"));
            File.WriteAllText(Path.Combine(fixture.Root, ".git", "hooks", "post-checkout"),
                "#!/bin/sh\ntouch '" + fixture.Hook.Replace('\\', '/') + "'\n");
            File.WriteAllText(Path.Combine(fixture.Root, "sample.txt"), "primary developer edit");
            File.WriteAllText(Path.Combine(fixture.Root, "user-local.txt"), "primary untracked work");
            fixture.BeforeStatus = await Command(fixture.Root, "status", "--porcelain=v1");
            fixture.BeforeRemote = await Command(fixture.Root, "rev-parse", "refs/remotes/origin/source");
            fixture.BeforeConfig = File.ReadAllText(Path.Combine(fixture.Root, ".git", "config"));
            return fixture;
        }

        public CopilotRunRequest Request(int id = 101, string prompt = "synthetic native first start") =>
            new("fixture:" + id, id, "Synthetic first start", "offline-org", "offline-project", "offline-repo", "offline",
                "refs/heads/source", Commit, prompt, Directory: Root, PushToSource: true) { NativeCopilotPath = Exe };

        public CopilotRunService Service(string? state = null) => new(stateDirectory: state ?? State)
        {
            NativeLocalRemoteForTests = Upstream,
            NativeLauncherForTests = specification => NativeFixSupervisor.Launch(specification, nestedSyntheticFixture: true)
        };

        public async Task AssertPrimaryUnchanged()
        {
            Check(await Command(Root, "rev-parse", "HEAD") == Commit, "Native preparation moved the primary HEAD");
            Check(await Command(Root, "symbolic-ref", "HEAD") == "refs/heads/source", "Native preparation switched the primary branch");
            Check(await Command(Root, "status", "--porcelain=v1") == BeforeStatus, "Native preparation changed primary/index edits");
            Check(await Command(Root, "rev-parse", "refs/remotes/origin/source") == BeforeRemote, "Native fetch rewrote primary remote-tracking refs");
            Check(File.ReadAllText(Path.Combine(Root, ".git", "config")) == BeforeConfig, "Native preparation changed Git configuration");
            Check(File.ReadAllText(Path.Combine(Root, "sample.txt")) == "primary developer edit" &&
                File.ReadAllText(Path.Combine(Root, "user-local.txt")) == "primary untracked work", "Native Fix discarded primary user work");
            Check(!File.Exists(Monitor) && !File.Exists(Hook), "Native preparation executed fsmonitor or checkout hooks");
        }

        public async ValueTask DisposeAsync()
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
            if (Directory.Exists(DirectoryPath))
            {
                foreach (var ownership in Directory.EnumerateFiles(DirectoryPath, "ownership.json", SearchOption.AllDirectories))
                {
                    var specification = NativeFixSupervisor.Read<NativeFixSupervisor.Specification>(ownership);
                    var receipt = NativeFixSupervisor.Status(specification);
                    if (receipt is not null && NativeFixSupervisor.SupervisorAlive(receipt))
                    {
                        NativeFixSupervisor.RequestCancel(specification);
                        await Until(() => !NativeFixSupervisor.SupervisorAlive(receipt));
                    }
                }
                foreach (var file in Directory.EnumerateFiles(DirectoryPath, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(DirectoryPath, true);
            }
        }
    }

    static void CopyPayload(string source, string appDirectory)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(appDirectory, "FixSupervisor", Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        File.WriteAllText(Path.Combine(appDirectory, "AdoPrView.exe"), "synthetic dashboard image");
        File.WriteAllText(Path.Combine(appDirectory, "AdoPrView.dll"), "synthetic dashboard assembly");
        File.WriteAllText(Path.Combine(appDirectory, "coreclr.dll"), "synthetic dashboard runtime");
    }

    static void RemoveApp(string appDirectory)
    {
        foreach (var file in Directory.EnumerateFiles(appDirectory, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(appDirectory, true);
    }

    internal static async Task Run(Func<string, Func<Task>, Task> test)
    {
        if (Environment.GetEnvironmentVariable("ADOPRVIEW_TEST_NATIVE_SCHEDULER_PROBE") == "1")
            await test("Native Fix Task Scheduler NEW start reconnects captured backend and exact Stop removes task without changing source", async () =>
            {
                await using var fixture = await LocalSource.Create();
                var portablePath = Path.GetDirectoryName(Git) + ";" + Path.Combine(fixture.DirectoryPath, "portable tools");
                Environment.SetEnvironmentVariable("PATH", portablePath);
                string id;
                NativeFixSupervisor.Specification specification;
                await using (var service = fixture.Service())
                {
                    service.NativeLauncherForTests = null;
                    if (Environment.GetEnvironmentVariable("ADOPRVIEW_TEST_NATIVE_SUPERVISOR_PAYLOAD") is { } payload)
                        service.NativeSupervisorAppDirectoryForTests = Path.GetDirectoryName(payload);
                    var run = service.Start(fixture.Request());
                    id = run.RunId;
                    await Until(() => File.Exists(Path.Combine(fixture.State, "native", id, "worktree", "fixture-child.pid")) ||
                        service.Get(id)!.Status is "failed" or "needs-attention");
                    Check(service.Get(id) is { Backend: "windows-native", MayBeRunning: true, Status: "running" },
                        "Real native Scheduler start failed: " + service.Get(id)!.Error);
                    specification = NativeFixSupervisor.Read<NativeFixSupervisor.Specification>(
                        Path.Combine(fixture.State, "native", id, "ownership.json"));
                    Check(specification.SupervisorLauncher == "task-scheduler" && NativeFixTaskBroker.Registered(specification),
                        "New native run did not pin its Scheduler launch ownership");
                    Check(specification.CliEnvironment!["PATH"] == portablePath &&
                        File.ReadAllText(Path.Combine(specification.Worktree, "fixture-path.txt")) == portablePath &&
                        specification.CliEnvironment.Keys.All(NativeFixEnvironment.IsAllowed),
                        "Scheduled CLI lost dashboard-only tool paths or captured non-allowlisted variables");
                    await fixture.AssertPrimaryUnchanged();
                }
                Check(NativeFixSupervisor.SupervisorAlive(NativeFixSupervisor.Status(specification)!),
                    "Disposing the first dashboard killed its Scheduler supervisor");
                await using var reopened = fixture.Service();
                await reopened.RecoverAsync();
                Check(reopened.Get(id) is { Backend: "windows-native", Status: "running", ResumeAttempts: 0 },
                    "Native Scheduler recovery switched backend or relaunched a prompt");
                var cancelled = await reopened.CancelAsync(id);
                Check(cancelled is { Status: "cancelled", MayBeRunning: false, WorktreeRetained: true },
                    "Reopened exact Stop did not preserve work and confirm inactivity");
                await Until(() => !NativeFixTaskBroker.Registered(specification));
                await fixture.AssertPrimaryUnchanged();
            });
        await test("Native Fix follower waits for supervisor cleanup and retains a late final receipt warning", async () =>
        {
            await using var fixture = await LocalSource.Create();
            await using var service = fixture.Service();
            System.Diagnostics.Process? supervisor = null;
            NativeFixSupervisor.Specification? specification = null;
            service.NativeLauncherForTests = owned =>
            {
                specification = owned;
                var start = new System.Diagnostics.ProcessStartInfo(Exe) { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add("--native-fix-delayed-cleanup");
                start.ArgumentList.Add(NativeFixSupervisor.FilePath(owned, "ownership.json"));
                supervisor = System.Diagnostics.Process.Start(start)!;
            };
            try
            {
                var run = service.Start(fixture.Request());
                await Until(() => specification is not null && NativeFixSupervisor.Status(specification) is { ExitCode: 0 });
                await Task.Delay(400);
                Check(service.Get(run.RunId)!.IsActive && NativeFixSupervisor.SupervisorAlive(NativeFixSupervisor.Status(specification!)!),
                    "Follower published completion while the supervisor was still cleaning up");
                File.WriteAllText(NativeFixSupervisor.FilePath(specification!, "release-cleanup"), "");
                await Until(() => service.Get(run.RunId)!.Status == "completed");
                Check(service.Get(run.RunId)!.Error!.Contains("delayed Scheduler cleanup failed") &&
                    service.Get(run.RunId)!.Output.Contains("delayed Scheduler cleanup failed"),
                    "Follower lost the cleanup warning written after the early terminal receipt");
            }
            finally
            {
                if (specification is not null) File.WriteAllText(NativeFixSupervisor.FilePath(specification, "release-cleanup"), "");
                if (supervisor is not null)
                {
                    await supervisor.WaitForExitAsync();
                    supervisor.Dispose();
                }
            }
        });
        await test("Native Fix uncertain NEW launch result preserves reservation and exact Stop without duplicate dispatch", async () =>
        {
            await using var fixture = await LocalSource.Create();
            await using var service = fixture.Service();
            var launches = 0;
            service.NativeLauncherForTests = s =>
            {
                launches++;
                NativeFixSupervisor.Launch(s, nestedSyntheticFixture: true);
                throw new NativeFixTaskBroker.DispatchException("Synthetic uncertain dispatch", new IOException("lost acknowledgement"));
            };
            var run = service.Start(fixture.Request());
            await Until(() => service.Get(run.RunId)!.Status == "needs-attention");
            Check(service.Get(run.RunId)!.MayBeRunning, "Uncertain dispatch was recorded as a definite no-child failure");
            try { service.Start(fixture.Request()); throw new Exception("Uncertain launch allowed a duplicate start"); }
            catch (InvalidOperationException) { }
            var stopped = await service.CancelAsync(run.RunId);
            Check(launches == 1 && stopped is { Status: "cancelled", MayBeRunning: false },
                "Exact Stop failed after uncertain launch or dispatch was retried");
        });
        await test("Native Fix NEW Start runs real local Git preparation, exact owned worktree and CLI without WSL", async () =>
        {
            await using var fixture = await LocalSource.Create();
            string? runId = null;
            await using (var service = fixture.Service())
            {
                try
                {
                    var run = service.Start(fixture.Request());
                    runId = run.RunId;
                    await Until(() => service.Get(run.RunId) is { Status: "running", WorktreePath: not null } state &&
                        File.Exists(Path.Combine(state.WorktreePath, "fixture-child.pid")));
                    var running = service.Get(run.RunId)!;
                    var worktree = running.WorktreePath!;
                    Check(running.Backend == "windows-native" && running.Distribution is null && running.SourcePath == fixture.Root,
                        "A new native run selected WSL or the wrong source root");
                    Check(File.ReadAllText(Path.Combine(worktree, "first-start-baseline.txt")) == "captured baseline",
                        "Native worktree checkout followed a replacement object instead of the captured commit");
                    Check(await Command(worktree, "rev-parse", "HEAD") == fixture.Commit &&
                        await Command(worktree, "symbolic-ref", "HEAD") == "refs/heads/ado-prview/pr-101-" + run.RunId,
                        "Native Start lost exact-commit or isolated branch ownership");
                    Check(running.Output.Contains("not an authentication check"), "Native help falsely claimed authentication readiness");
                    await fixture.AssertPrimaryUnchanged();
                    var stopped = await service.CancelAsync(run.RunId);
                    Check(stopped is { Status: "cancelled", MayBeRunning: false, WorktreeRetained: true } &&
                        File.ReadAllText(Path.Combine(worktree, "native-fix-edit.txt")) == "synthetic owned fix edit",
                        "Native first-start Stop lost owned edits or failed to prove descendant exit");
                    var retry = service.Start(fixture.Request(prompt: "synthetic-complete"));
                    await Until(() => service.Get(retry.RunId)!.Status == "completed");
                    Check(service.Get(retry.RunId)!.WorktreePath != worktree &&
                        service.Get(retry.RunId) is { ResumeAttempts: 0, MayBeRunning: false }, "A deliberate retry reused the old workspace/session");
                    await fixture.AssertPrimaryUnchanged();
                }
                finally
                {
                    if (runId is not null) await service.CancelAsync(runId);
                }
            }
            await using var reopened = fixture.Service();
            await reopened.RecoverAsync();
            Check(reopened.Get(runId!) is { Status: "cancelled", MayBeRunning: false, ResumeAttempts: 0 },
                "A real native first-start record was replayed after reopen");
            await fixture.AssertPrimaryUnchanged();
            Console.WriteLine("NATIVE-START real Git pinned checkout, replacement/fsmonitor/hook protection, exact Stop, fresh retry and unchanged primary source verified");
        });

        await test("Native Fix NEW Start rejects root remote push and stale revision before creating a worktree", async () =>
        {
            await using var fixture = await LocalSource.Create();
            foreach (var failure in new[] { "non-git", "nested", "remote", "push", "stale" })
            {
                var directory = Path.Combine(fixture.DirectoryPath, failure);
                Directory.CreateDirectory(directory);
                var request = fixture.Request(200 + Array.IndexOf(new[] { "non-git", "nested", "remote", "push", "stale" }, failure));
                if (failure == "non-git")
                {
                    File.WriteAllText(Path.Combine(directory, ".git"), "invalid synthetic Git directory pointer");
                    request = request with { Directory = directory };
                }
                if (failure == "nested")
                {
                    directory = Path.Combine(fixture.Root, "nested");
                    Directory.CreateDirectory(directory);
                    request = request with { Directory = directory };
                }
                if (failure == "stale") request = request with { SourceCommit = fixture.Poison };
                if (failure == "push") await Command(fixture.Root, "config", "remote.origin.pushurl", fixture.Other);
                await using var service = fixture.Service(Path.Combine(fixture.DirectoryPath, "state-" + failure));
                if (failure == "remote") service.NativeLocalRemoteForTests = fixture.Other;
                service.NativeLauncherForTests = _ => throw new Exception("Rejected preparation unexpectedly launched a CLI");
                try
                {
                    var started = service.Start(request);
                    await Until(() => service.Get(started.RunId)!.Status == "failed");
                    var state = service.Get(started.RunId)!;
                    Check(state.WorktreePath is null && !state.MayBeRunning && state.Backend == "windows-native",
                        "Rejected " + failure + " preparation claimed a worktree or task");
                    Check(failure switch
                    {
                        "non-git" => state.Error!.Contains("repository-root exited with code 128"),
                        "nested" => state.Error!.Contains("repository root"),
                        "stale" => state.Error!.Contains("source branch no longer matches"),
                        _ => state.Error!.Contains("no credential-free Git remote")
                    }, "Rejected " + failure + " preparation lost its actionable native stage diagnostic: " + state.Error);
                }
                finally
                {
                    if (failure == "push") await Command(fixture.Root, "config", "--unset", "remote.origin.pushurl");
                    if (failure == "nested") Directory.Delete(directory);
                }
            }
            await fixture.AssertPrimaryUnchanged();
        });

        await test("Native Fix NEW Start distinguishes native CLI capability from authentication and preserves prepared work", async () =>
        {
            await using var fixture = await LocalSource.Create();
            await using var service = fixture.Service();
            var start = service.Start(fixture.Request(prompt: "synthetic-auth-failure"));
            await Until(() => service.Get(start.RunId)!.Status == "failed");
            var failed = service.Get(start.RunId)!;
            Check(failed is { ExitCode: 1, MayBeRunning: false, WorktreeRetained: true } &&
                failed.Output.Contains("not an authentication check") && failed.Error!.Contains("chosen native Windows Copilot CLI") &&
                failed.Error.Contains("copilot login") && !failed.Error.Contains("gh auth login"),
                "Actual selected CLI authentication failure borrowed another host's auth or falsely claimed readiness");
            Check(File.ReadAllText(Path.Combine(failed.WorktreePath!, "native-fix-edit.txt")) == "synthetic owned fix edit" &&
                !File.Exists(Path.Combine(fixture.State, "native", start.RunId, "prompt")), "Native auth failure discarded prepared work or retained the prompt");
            await fixture.AssertPrimaryUnchanged();
        });

        await test("Native Fix NEW Start rejects missing supervisor and declared runtime dependencies before any owned worktree", async () =>
        {
            await using var fixture = await LocalSource.Create();
            foreach (var missing in new[] { "payload", "runtime" })
            {
                var app = Path.Combine(fixture.DirectoryPath, "broken app " + missing);
                Directory.CreateDirectory(app);
                if (missing == "runtime")
                {
                    CopyPayload(Path.Combine(AppContext.BaseDirectory, "FixSupervisor"), app);
                    File.WriteAllText(Path.Combine(app, "FixSupervisor", "AdoPrView.FixSupervisor.deps.json"),
                        """{"runtimeTarget":{"name":"fixture"},"targets":{"fixture":{"Fixture/1":{"runtime":{"synthetic-missing-runtime.dll":{}}}}}}""");
                }
                await using var service = fixture.Service(Path.Combine(fixture.DirectoryPath, "state-" + missing));
                service.NativeSupervisorAppDirectoryForTests = app;
                var start = service.Start(fixture.Request());
                await Until(() => service.Get(start.RunId)!.Status == "failed");
                var failed = service.Get(start.RunId)!;
                Check(failed is { MayBeRunning: false, WorktreePath: null, WorktreeStatus: "not-created" } &&
                    failed.Error!.Contains(missing == "runtime" ? "declared runtime dependencies are incomplete" : "supervisor payload is missing"),
                    "Missing separate supervisor prerequisite started a worktree/task or lost actionable deployment guidance");
                await fixture.AssertPrimaryUnchanged();
            }
        });

        await test("Native Fix known failed NEW launcher remains terminal after recovery without a receipt or PR reservation", async () =>
        {
            await using var fixture = await LocalSource.Create();
            string id;
            await using (var service = fixture.Service())
            {
                service.NativeLauncherForTests = _ => throw new NativeFixSupervisor.LaunchException(5, "Synthetic definite CreateProcess failure");
                var started = service.Start(fixture.Request());
                id = started.RunId;
                await Until(() => service.Get(id)!.Status == "failed");
                Check(service.Get(id) is { MayBeRunning: false } &&
                    !File.Exists(Path.Combine(fixture.State, "native", id, "receipt.json")), "Known no-child launch became uncertain");
            }
            await using var reopened = fixture.Service();
            await reopened.RecoverAsync();
            Check(reopened.Get(id) is { Status: "failed", MayBeRunning: false }, "Failed native launcher was falsely reattached as running");
            var next = reopened.Start(fixture.Request(prompt: "synthetic-complete"));
            await Until(() => reopened.Get(next.RunId)!.Status == "completed");
            Check(next.RunId != id && reopened.Get(id)!.ResumeAttempts == 0, "A definite no-child failure restored a permanent PR reservation");
            await fixture.AssertPrimaryUnchanged();
        });

        await test("Native Fix separate stable supervisor survives disposable app upgrade and exact new-dashboard Stop", async () =>
        {
            await using var fixture = await LocalSource.Create();
            var payload = Environment.GetEnvironmentVariable("ADOPRVIEW_TEST_NATIVE_SUPERVISOR_PAYLOAD") ??
                Path.Combine(AppContext.BaseDirectory, "FixSupervisor");
            var app = Path.Combine(fixture.DirectoryPath, "deployed dashboard");
            CopyPayload(payload, app);
            File.WriteAllText(Path.Combine(app, "FixSupervisor", "copied-account-config.json"), "synthetic configuration must not be copied");
            string id;
            NativeFixSupervisor.Specification original;
            await using (var first = fixture.Service())
            {
                first.NativeSupervisorAppDirectoryForTests = app;
                var start = first.Start(fixture.Request());
                id = start.RunId;
                await Until(() => first.Get(id) is { Status: "running", WorktreePath: not null } state &&
                    File.Exists(Path.Combine(state.WorktreePath, "fixture-child.pid")));
                original = NativeFixSupervisor.Read<NativeFixSupervisor.Specification>(
                    Path.Combine(fixture.State, "native", id, "ownership.json"));
                Check(original.SupervisorExecutable is not null && original.SupervisorBundleId is not null &&
                    first.Get(id)!.SupervisorDeployment == "stable:" + original.SupervisorBundleId,
                    "New native run used the upgrade-blocking dashboard executable");
                var receipt = NativeFixSupervisor.Status(original)!;
                Check(!File.Exists(Path.Combine(Path.GetDirectoryName(original.SupervisorExecutable)!, "copied-account-config.json")),
                    "Stable supervisor deployment copied non-runtime account/configuration files");
                try
                {
                    NativeFixSupervisor.Launch(original, supervisorExe: Exe, nestedSyntheticFixture: true);
                    throw new Exception("Stable run accepted an app-host or duplicate supervisor override");
                }
                catch (NativeFixSupervisor.OwnershipException) { }
                using var supervisor = System.Diagnostics.Process.GetProcessById(receipt.SupervisorPid);
                Check(supervisor.StartTime.ToUniversalTime().Ticks == receipt.SupervisorStartedTicks &&
                    supervisor.MainModule!.FileName == original.SupervisorExecutable, "Supervisor did not load from its owned stable deployment");
                if (File.Exists(Path.Combine(payload, "coreclr.dll")))
                    Check(supervisor.Modules.Cast<System.Diagnostics.ProcessModule>().Any(module =>
                        module.ModuleName.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase) &&
                        Path.GetDirectoryName(module.FileName) == Path.GetDirectoryName(original.SupervisorExecutable)),
                        "Self-contained supervisor loaded CoreCLR from the replaceable dashboard deployment");
            }
            RemoveApp(app);
            Check(NativeFixSupervisor.ActiveProcesses(original) >= 2, "Deleting/replacing app files stopped the detached native job");
            CopyPayload(payload, app);
            File.WriteAllText(Path.Combine(app, "FixSupervisor", "THIRD-PARTY-NOTICES.txt"), "synthetic updated bundle generation");
            await using var upgraded = fixture.Service();
            upgraded.NativeSupervisorAppDirectoryForTests = app;
            await upgraded.RecoverAsync();
            Check(upgraded.Get(id) is { Status: "running", ResumeAttempts: 0 } &&
                Directory.Exists(Path.GetDirectoryName(original.SupervisorExecutable)), "New dashboard migrated/restarted/pruned the active old supervisor");
            var stopped = await upgraded.CancelAsync(id);
            Check(stopped is { Status: "cancelled", MayBeRunning: false, WorktreeRetained: true } &&
                NativeFixSupervisor.ActiveProcesses(original) is null or 0, "Upgraded dashboard did not stop the exact retained job");
            var next = upgraded.Start(fixture.Request(prompt: "synthetic-complete"));
            await Until(() => upgraded.Get(next.RunId)!.Status == "completed");
            var fresh = NativeFixSupervisor.Read<NativeFixSupervisor.Specification>(
                Path.Combine(fixture.State, "native", next.RunId, "ownership.json"));
            Check(fresh.SupervisorBundleId != original.SupervisorBundleId &&
                Directory.Exists(Path.GetDirectoryName(original.SupervisorExecutable)) &&
                Directory.Exists(Path.GetDirectoryName(fresh.SupervisorExecutable)), "Upgrade reused mutable code or removed an old owned deployment");
            await fixture.AssertPrimaryUnchanged();
            Console.WriteLine("NATIVE-UPGRADE self-contained=" + File.Exists(Path.Combine(payload, "coreclr.dll")) +
                "; app deployment deleted/replaced while old job survived; new dashboard reattached/stopped exact run; old/new bundles retained");
        });
    }
}
