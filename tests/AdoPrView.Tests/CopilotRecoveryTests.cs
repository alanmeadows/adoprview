using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using AdoPrView;

static class CopilotRecoveryTests
{
    const string Help = "--no-ask-user --stream --deny-tool --yolo --session-id --resume; piped stdin; non-interactive mode cannot ask";
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static CopilotRunRequest Request() => new("repo-id:12", 12, "Saved offline PR", "offline-org", "offline-project",
        "offline-repo", "repo-id", "refs/heads/topic", new string('1', 40), "ORIGINAL PRIVATE PROMPT",
        "Offline-Ubuntu", "/home/offline/source");

    static async Task<CopilotRunState> Wait(CopilotRunService service, string id, Func<CopilotRunState, bool> predicate)
    {
        var ready = new TaskCompletionSource<CopilotRunState>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(CopilotRunState state) { if (state.RunId == id && predicate(state)) ready.TrySetResult(state); }
        service.Changed += Changed;
        try { if (service.Get(id) is { } current) Changed(current); return await ready.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (TimeoutException) { throw new Exception($"Recovery did not reach expected state: {service.Get(id)?.Status}: {service.Get(id)?.Error}"); }
        finally { service.Changed -= Changed; }
    }

    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("Copilot durable restart reattaches output without a second launch and cancel remains identity-bound", async () =>
        {
            using var f = new Fixture();
            var first = f.Service();
            var run = await first.StartAsync(Request());
            await Wait(first, run.RunId, s => s.Output.Contains("FAKE-READY"));
            f.Transport.Emit("saved output");
            await first.DisposeAsync();
            Check(f.Transport.Stops == 0 && f.Transport.Launches == 1, "Dashboard exit stopped/relaunched its supervised job");
            Check(!File.ReadAllText(f.Record(run.RunId)).Contains(Request().Prompt), "Original prompt was persisted in Windows history");
            Check(f.Store.Load().Single().Saved!.Environment!.Help == "", "CLI help was unnecessarily persisted in minimal launch metadata");
            await using var second = f.Service();
            await second.RecoverAsync();
            var attached = await Wait(second, run.RunId, s => s.IsActive && s.Output.Contains("saved output"));
            Check(attached.SessionId == run.SessionId && attached.WorktreePath == first.Get(run.RunId)!.WorktreePath &&
                f.Transport.Launches == 1, "Reattachment changed session/worktree or launched again");
            f.Transport.Emit("live after restart");
            await Wait(second, run.RunId, s => s.Output.Contains("live after restart"));
            try { await second.CancelAsync("wrong-row", run.RunId); throw new Exception("Wrong row cancelled recovered run"); }
            catch (InvalidOperationException) { }
            var stopped = await second.CancelAsync(run.Key, run.RunId);
            Check(stopped!.Status == "cancelled" && f.Transport.Stops == 1 && stopped.WorktreeRetained, "Recovered cancel lost work or wrong outcome");
        });

        await test("Copilot interrupted run resumes exact saved ID once in same CWD without replaying prompt or changing reviewers", async () =>
        {
            using var f = new Fixture();
            var first = f.Service();
            var run = await first.StartAsync(Request());
            await Wait(first, run.RunId, s => s.Output.Contains("FAKE-READY"));
            await first.DisposeAsync();
            f.Transport.End(143, outcome: false);
            await using var second = f.Service();
            await second.RecoverAsync();
            await Wait(second, run.RunId, s => s.IsActive && s.ResumeAttempts == 1);
            var launch = f.Transport.Calls.Last(c => c.Operation == "launch");
            Check(launch.Arguments[6] == run.SessionId && launch.Arguments[8] == "resume" &&
                launch.Arguments[2] == first.Get(run.RunId)!.WorktreePath &&
                f.Transport.Calls.Count(c => c.Operation == "stage-prompt") == 1,
                "Resume changed exact session/CWD or replayed the original/new generated prompt");
            Check(CopilotRunService.RunnerScript.Contains("--resume \"$session\" --prompt") &&
                CopilotRunService.RunnerScript.Contains("preserve its original instructions and scope"), "Resume is not a supported exact-ID continuation");
            f.Transport.Cleanup = "RETAINED";
            f.Transport.End(0);
            await Wait(second, run.RunId, s => s.Status == "completed" && s.WorktreeStatus == "retained-changes");
            await second.DisposeAsync();
            await using var third = f.Service();
            await third.RecoverAsync();
            Check(third.Get(run.RunId)!.Status == "completed" && f.Transport.Launches == 2,
                "Completed-but-retained run was resumed to make cleanup possible");
        });

        foreach (var push in new[] { true, false })
            await test($"Copilot persisted push authority survives restart into the exact-session resume argv; older records stay no-push (push={push})", async () =>
            {
                using var f = new Fixture();
                var first = f.Service();
                var run = await first.StartAsync(Request() with { PushToSource = push });
                await Wait(first, run.RunId, s => s.Output.Contains("FAKE-READY"));
                Check(f.Transport.Calls.Single(c => c.Operation == "launch").Arguments[8] == (push ? "start-push" : "start"), "Initial launch lost its authority");
                await first.DisposeAsync();
                var record = JsonNode.Parse(File.ReadAllText(f.Record(run.RunId)))!;
                var request = record["State"]!["Request"]!.AsObject();
                Check(request["PushToSource"]!.GetValue<bool>() == push && request["Prompt"]!.GetValue<string>() == "", "Push authority was not persisted with the run");
                if (!push)
                {
                    // A record written before push authority existed has no such member.
                    request.Remove("PushToSource");
                    File.WriteAllText(f.Record(run.RunId), record.ToJsonString());
                }
                f.Transport.End(143, outcome: false);
                await using var second = f.Service();
                await second.RecoverAsync();
                var resumed = await Wait(second, run.RunId, s => s.IsActive && s.ResumeAttempts == 1);
                var launch = f.Transport.Calls.Last(c => c.Operation == "launch");
                Check(launch.Arguments[8] == (push ? "resume-push" : "resume") && resumed.Request.PushToSource == push &&
                    resumed.PermissionNotice == (push ? CopilotRunService.PushNotice : CopilotRunService.HeadlessNotice) &&
                    f.Transport.Calls.Count(c => c.Operation == "stage-prompt") == 1,
                    "Resume changed the run's push authority or replayed a prompt");
            });

        await test("Copilot recovery does not adopt a task whose runner authority differs from its saved record", async () =>
        {
            using var f = new Fixture();
            var first = f.Service();
            var run = await first.StartAsync(Request());
            await Wait(first, run.RunId, s => s.Output.Contains("FAKE-READY"));
            await first.DisposeAsync();
            var record = JsonNode.Parse(File.ReadAllText(f.Record(run.RunId)))!;
            record["State"]!["Request"]!["PushToSource"] = true;
            File.WriteAllText(f.Record(run.RunId), record.ToJsonString());
            await using var second = f.Service();
            await second.RecoverAsync();
            var refused = await Wait(second, run.RunId, s => s.Error is not null);
            Check(refused.Error!.Contains("identity") && f.Transport.Launches == 1 && f.Transport.Stops == 0 && f.Transport.Cleanups == 0,
                "A task launched with different push authority was adopted, relaunched, stopped or cleaned: " + refused.Error);
        });

        foreach (var code in new[] { 0, 1, 143 })
            await test($"Copilot task finishing while GUI is closed stays terminal and never resumes (exit={code})", async () =>
            {
                using var f = new Fixture();
                var first = f.Service();
                var run = await first.StartAsync(Request());
                await Wait(first, run.RunId, s => s.Output.Contains("FAKE-READY"));
                await first.DisposeAsync();
                if (code == 143) f.Store.RequestCancel(run.RunId);
                f.Transport.End(code);
                await using var recovered = f.Service();
                await recovered.RecoverAsync();
                await Wait(recovered, run.RunId, s => !s.IsActive);
                Check(recovered.Get(run.RunId)!.Status == (code == 0 ? "completed" : code == 143 ? "cancelled" : "failed") &&
                    f.Transport.Launches == 1, "A genuine terminal/cancelled task was resumed");
                if (code == 0)
                    Check(f.Transport.Cleanups == 1 && recovered.Get(run.RunId)!.WorktreeStatus == "removed-clean" &&
                        recovered.Get(run.RunId)!.Output.Contains("FAKE-READY"), "Startup did not clean a safe success or replay terminal logs");
                else Check(f.Transport.Cleanups == 0, "Failed/cancelled work was cleaned on startup");
            });

        foreach (var fault in new[] { "session", "wsl", "status", "ownership", "held-lock", "resume-used" })
            await test("Copilot recovery preserves work and reports blocker: " + fault, async () =>
            {
                using var f = new Fixture();
                var first = f.Service();
                var run = await first.StartAsync(Request());
                await Wait(first, run.RunId, s => s.Output.Contains("FAKE-READY"));
                await first.DisposeAsync();
                f.Transport.End(137, outcome: false);
                if (fault == "session") f.Transport.SessionAvailable = false;
                if (fault == "wsl") f.Transport.Failure = "preflight";
                if (fault == "status") f.Transport.Failure = "status";
                if (fault == "ownership") f.Transport.Failure = "verify-run";
                if (fault == "held-lock") f.Transport.LockHeld = true;
                if (fault == "resume-used")
                {
                    var saved = f.Store.Load().Single().Saved!;
                    f.Store.Write(saved with { State = saved.State with { ResumeAttempts = 1 } });
                }
                await using var recovered = f.Service();
                await recovered.RecoverAsync();
                Check(recovered.Get(run.RunId)!.Status == "needs-attention" && recovered.Get(run.RunId)!.WorktreeRetained &&
                    f.Transport.Launches == 1 && f.Transport.Cleanups == 0, "Ambiguous recovery launched/deleted work or hid its blocker");
                try { await recovered.StartAsync(Request()); throw new Exception("Lost durable PR reservation"); }
                catch (InvalidOperationException) { }
            });

        await test("Copilot terminal completion is durable before failing cleanup and stays terminal on later startup errors", async () =>
        {
            using var f = new Fixture();
            var first = f.Service();
            var run = await first.StartAsync(Request());
            await Wait(first, run.RunId, s => s.Output.Contains("FAKE-READY"));
            f.Transport.Failure = "cleanup";
            f.Transport.End(0);
            await Wait(first, run.RunId, s => s.Status == "completed" && s.WorktreeStatus == "retained-changes");
            Check(f.Store.Load().Single().Saved!.State.Status == "completed", "Cleanup failure corrupted durable terminal outcome");
            await first.DisposeAsync();
            f.Transport.Failure = "preflight";
            await using var recovered = f.Service();
            await recovered.RecoverAsync();
            Check(recovered.Get(run.RunId)!.Status == "completed" && f.Transport.Launches == 1, "Terminal record became a resumable interruption");
        });

        await test("Copilot concurrent dashboard instances reserve one PR and perform at most one exact-session resume", async () =>
        {
            using var f = new Fixture();
            var first = f.Service();
            var run = await first.StartAsync(Request());
            await Wait(first, run.RunId, s => s.Output.Contains("FAKE-READY"));
            await using (var other = f.Service())
            {
                try { await other.StartAsync(Request()); throw new Exception("Concurrent dashboard started a duplicate PR"); }
                catch (InvalidOperationException) { }
            }
            await first.DisposeAsync();
            f.Transport.End(143, outcome: false);
            await using var a = f.Service();
            await using var b = f.Service();
            await Task.WhenAll(a.RecoverAsync(), b.RecoverAsync());
            Check(f.Transport.Launches == 2, "Concurrent recovery resumed the same session twice");
            f.Transport.End(0);
            await Wait(a.Get(run.RunId)!.IsActive ? a : b, run.RunId, s => s.Status == "completed");
            Check(f.Transport.Cleanups <= 1, "Concurrent recovery cleaned the same worktree twice");
        });

        await test("Copilot corrupt and partial launch history cannot trigger recovery, cleanup or duplicate starts", async () =>
        {
            using var f = new Fixture();
            Directory.CreateDirectory(f.Directory);
            File.WriteAllText(f.Record(new string('a', 32)), "{\"Version\":1,");
            await using var service = f.Service();
            await service.RecoverAsync();
            Check(service.Runs.Single().Status == "needs-attention" && f.Transport.Calls.IsEmpty, "Corrupt history guessed a live task");
            try { await service.StartAsync(Request()); throw new Exception("Corrupt record did not reserve uncertain work"); }
            catch (InvalidOperationException) { }
        });

        await test("Copilot interrupted never-launched preparation is preserved and can be explicitly cancelled without stopping a task", async () =>
        {
            using var f = new Fixture();
            var first = f.Service();
            f.Transport.Failure = "preflight";
            var run = await first.StartAsync(Request());
            await Wait(first, run.RunId, s => s.Status == "failed");
            await first.DisposeAsync();
            var saved = f.Store.Load().Single().Saved!;
            f.Store.Write(saved with { State = saved.State with { Status = "preparing" } });
            await using var recovered = f.Service();
            await recovered.RecoverAsync();
            Check(recovered.Get(run.RunId)!.Status == "needs-attention", "Interrupted preparation silently became a new prompt");
            var cancelled = await recovered.CancelAsync(run.Key, run.RunId);
            Check(cancelled!.Status == "cancelled" && f.Transport.Launches == 0 && f.Transport.Stops == 0,
                "Cancelling a proven never-launched reservation controlled an unknown task");
            f.Transport.Failure = null;
            var replacement = await recovered.StartAsync(Request());
            await Wait(recovered, replacement.RunId, s => s.IsActive && s.Output.Contains("FAKE-READY"));
        });

        await test("Copilot durable cancel intent survives a dashboard crash before the task actually stops", async () =>
        {
            using var f = new Fixture();
            var first = f.Service();
            var run = await first.StartAsync(Request());
            await Wait(first, run.RunId, s => s.Output.Contains("FAKE-READY"));
            await first.DisposeAsync();
            f.Store.RequestCancel(run.RunId);
            await using var recovered = f.Service();
            await recovered.RecoverAsync();
            Check(recovered.Get(run.RunId)!.Status == "cancelled" && f.Transport.Launches == 1 &&
                f.Transport.Stops == 1 && f.Transport.Cleanups == 0,
                "Startup ignored explicit cancellation or resumed/cleaned its work");
        });

        await test("Copilot failed resume or missing authentication is needs-attention, retains work and never loops", async () =>
        {
            using var f = new Fixture();
            var first = f.Service();
            var run = await first.StartAsync(Request());
            await Wait(first, run.RunId, s => s.Output.Contains("FAKE-READY"));
            await first.DisposeAsync();
            f.Transport.End(143, outcome: false);
            await using var resumed = f.Service();
            await resumed.RecoverAsync();
            f.Transport.Emit("Authentication required; sign in using the CLI.");
            f.Transport.End(1);
            await Wait(resumed, run.RunId, s => s.Status == "needs-attention");
            await resumed.DisposeAsync();
            await using var again = f.Service();
            await again.RecoverAsync();
            Check(again.Get(run.RunId)!.Status == "needs-attention" && again.Get(run.RunId)!.WorktreeRetained &&
                f.Transport.Launches == 2 && f.Transport.Cleanups == 0,
                "Authentication/continuation failure restarted in a loop or lost work");
        });

        await test("Copilot crash after successful worktree removal keeps terminal history and replays its logs without resume", async () =>
        {
            using var f = new Fixture();
            var first = f.Service();
            var run = await first.StartAsync(Request());
            await Wait(first, run.RunId, s => s.Output.Contains("FAKE-READY"));
            f.Transport.End(0);
            await Wait(first, run.RunId, s => s.WorktreeStatus == "removed-clean");
            await first.DisposeAsync();
            var saved = f.Store.Load().Single().Saved!;
            f.Store.Write(saved with { State = saved.State with { WorktreeStatus = "in-use" } });
            await using var recovered = f.Service();
            await recovered.RecoverAsync();
            var final = recovered.Get(run.RunId)!;
            Check(final.Status == "completed" && final.WorktreeStatus == "removed-clean" &&
                final.Output.Contains("FAKE-READY") && f.Transport.Launches == 1 && f.Transport.Cleanups == 1,
                "Post-cleanup crash lost inline logs, re-cleaned or resumed a completed session");
        });
    }

    sealed class Fixture : IDisposable
    {
        public readonly string Directory = Path.Combine(Environment.CurrentDirectory, ".copilot-recovery-fixture-" + Guid.NewGuid().ToString("N"));
        public readonly Fake Transport = new();
        public CopilotRunService.DurableStore Store => new(Directory);
        public CopilotRunService Service() => new(Transport, Directory);
        public string Record(string id) => Path.Combine(Directory, id + ".json");
        public void Dispose() { if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true); }
    }

    sealed class Fake : ICopilotRunTransport
    {
        readonly object gate = new();
        public readonly ConcurrentQueue<CopilotProcessRequest> Calls = new();
        readonly List<Action<string>> readers = [];
        TaskCompletionSource<int> ended = NewEnd();
        string[] launch = [];
        int serial;
        string log = "FAKE-READY\n";
        int? outcome;
        public int Launches, Stops, Cleanups;
        public string? Failure;
        public string Cleanup = "REMOVED";
        public bool SessionAvailable = true, LockHeld, WorkspaceRemoved;
        static TaskCompletionSource<int> NewEnd() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Emit(string value) { lock (gate) { log += value; foreach (var read in readers.ToArray()) read(value); } }
        public void End(int code, bool outcome = true)
        {
            lock (gate) { this.outcome = outcome ? code : null; ended.TrySetResult(code); }
        }
        public async Task<CopilotProcessResult> ExecuteAsync(CopilotProcessRequest r, Action<string>? stdout, Action<string>? stderr, CancellationToken token)
        {
            Calls.Enqueue(r);
            token.ThrowIfCancellationRequested();
            if (Failure == r.Operation) return new(1, "", "never expose this transport detail");
            string Protocol(params string[] values) => "ADOPRVIEW1\0" + string.Join('\0', values) + "\0";
            switch (r.Operation)
            {
                case "preflight": return new(0, Protocol("Offline-Ubuntu", "offline", "/home/offline", "/home/offline/source",
                    "/home/offline/source/.git", "/home/offline/bin/copilot", "/home/offline/bin/bgtask", Help, "/home/offline/.copilot",
                    "origin", "https://dev.azure.com/offline-org/offline-project/_git/offline-repo",
                    "https://dev.azure.com/offline-org/offline-project/_git/offline-repo", new string('a', 64)), "");
                case "prepare": return new(0, Protocol("WORKTREE", "/home/offline/.local/state/ado-prview/runs/" + r.Arguments[6],
                    "/home/offline/.local/state/ado-prview/runs/" + r.Arguments[6] + "/worktree"), "");
                case "launch":
                    lock (gate)
                    {
                        if (Launches > 0) ended = NewEnd();
                        launch = r.Arguments.ToArray(); Launches++; serial++; outcome = null;
                    }
                    return new(0, "", "");
                case "verify-run":
                    return new(0, Protocol(WorkspaceRemoved ? "REMOVED" : "OWNED", LockHeld || !ended.Task.IsCompleted ? "LOCKED" : "FREE",
                        SessionAvailable ? "SESSION" : "MISSING", outcome?.ToString() ?? "NONE"), "");
                case "status":
                    lock (gate)
                    {
                        return new(0, JsonSerializer.Serialize(new
                        {
                            id = "fake-task-" + serial, name = launch[1], cwd = launch[2], log = "/home/offline/supervised/log",
                            command = new[] { "bash", "--noprofile", "--norc", "-c", launch[5], "ado-prview-runner",
                                launch[3], launch[4], launch[6], launch[7], launch[8] },
                            status = ended.Task.IsCompletedSuccessfully ?
                                (object)new { state = "exited", exited = new { code = ended.Task.Result } } :
                                new { state = "running" }
                        }), "");
                    }
                case "follow":
                    Task done;
                    lock (gate) { if (stdout is not null) { readers.Add(stdout); stdout(log); } done = ended.Task; }
                    try { await done.WaitAsync(token); return new(0, "", ""); }
                    finally { lock (gate) { if (stdout is not null) readers.Remove(stdout); } }
                case "history": stdout?.Invoke(log); return new(0, log, "");
                case "stop": Stops++; End(143); return new(0, "", "");
                case "cleanup":
                    Check(ended.Task.IsCompleted, "Cleanup attempted on a live fake session");
                    if (Cleanup == "REMOVED") WorkspaceRemoved = true;
                    Cleanups++; return new(0, Protocol(Cleanup, "fake retained work"), "");
                default: return new(0, "", "");
            }
        }
    }
}
