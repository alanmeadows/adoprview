using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using AdoPrView;

static class CopilotRecoveryTests
{
    const string Help = "--no-ask-user --stream --no-color --deny-tool --yolo --session-id --resume";
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
        foreach (var recoveredRun in new[] { false, true })
            foreach (var stop in new[] { true, false })
                await test($"Copilot completed cleanup {(recoveredRun ? "recovered" : "normal")} {(stop ? "Stop" : "disposal")} stays bounded", async () =>
                {
                    using var f = new Fixture();
                    f.Transport.Cleanup = "RETAINED";
                    await using var first = f.Service();
                    var run = await first.StartAsync(Request());
                    await Wait(first, run.RunId, s => s.Output.Contains("FAKE-READY"));
                    if (recoveredRun)
                    {
                        f.Transport.End(0);
                        await Wait(first, run.RunId, s => s.Status == "completed");
                        await first.DisposeAsync();
                    }
                    var cleanups = f.Transport.Cleanups;
                    f.Transport.BlockCleanup = true;
                    await using var restored = recoveredRun ? f.Service() : null;
                    var service = restored ?? first;
                    Task? recovery = null, action = null;
                    System.Windows.Forms.ToolStripMenuItem[] items = [];
                    try
                    {
                        if (recoveredRun) recovery = service.RecoverAsync();
                        else f.Transport.End(0);
                        await f.Transport.CleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                        var before = service.Get(run.RunId)!;
                        Check(before is { Status: "completed", MayBeRunning: false, ExitCode: 0 } &&
                            f.Store.Load().Single().Saved!.TaskConfirmedStopped,
                            "Cleanup fixture did not publish verified completion before the blocked transport");
                        if (stop)
                        {
                            items = MainWindow.CopilotStopMenuItems([run],
                                selected => action = service.CancelAsync(selected.Key, selected.RunId));
                            Check(items.Length == 1 && items[0].Enabled, "Captured cleanup Stop lost its exact target");
                            items[0].PerformClick();
                        }
                        else action = service.DisposeAsync().AsTask();
                        await (action ?? throw new Exception("Cleanup control action was not invoked"))
                            .WaitAsync(TimeSpan.FromSeconds(3));
                        var final = service.Get(run.RunId)!;
                        Check(final is { Status: "completed", MayBeRunning: false, ExitCode: 0, WorktreeRetained: true } &&
                            final.Error?.Contains("cleanup") == true && final.Output == before.Output &&
                            final.OutputOffset == before.OutputOffset && !f.Transport.WorkspaceRemoved &&
                            f.Transport.Launches == 1 && f.Transport.Stops == 0 && f.Transport.Cleanups == cleanups + 1 &&
                            f.Store.Load().Single().Saved!.TaskConfirmedStopped,
                            "Interrupted cleanup lost completed outcome, output/work/stop proof or warning");
                        f.Transport.ReleaseCleanup.TrySetResult();
                        await f.Transport.CleanupReturned.Task.WaitAsync(TimeSpan.FromSeconds(3));
                        Check(service.Get(run.RunId) is { WorktreeRetained: true } late && late.Error == final.Error &&
                            late.Output == final.Output && late.OutputOffset == final.OutputOffset,
                            "Abandoned cleanup accepted a late REMOVED reply or changed completion output");
                        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                    }
                    finally
                    {
                        f.Transport.ReleaseCleanup.TrySetResult();
                        foreach (var item in items) item.Dispose();
                        if (recovery is not null) await recovery.WaitAsync(TimeSpan.FromSeconds(3));
                        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                        if (action is not null) await action.WaitAsync(TimeSpan.FromSeconds(3));
                    }
                });
        await test("Copilot terminal history pending after exit stops from the captured native menu", async () =>
        {
            foreach (var (terminal, code) in new[] { ("failed", 1), ("completed", 0), ("cancelled", 143) })
            {
                using var f = new Fixture();
                f.Transport.Cleanup = "RETAINED";
                await using var first = f.Service();
                var run = await first.StartAsync(Request());
                await Wait(first, run.RunId, s => s.Output.Contains("FAKE-READY"));
                f.Transport.End(code);
                await Wait(first, run.RunId, s => s.Status == (code == 0 ? "completed" : "failed"));
                await first.DisposeAsync();
                var saved = f.Store.Load().Single().Saved!;
                f.Store.Write(saved with { State = saved.State with { Status = terminal } });
                var cleanups = f.Transport.Cleanups;
                f.Transport.BlockHistory = true;
                await using var recovered = f.Service();
                var recovery = recovered.RecoverAsync();
                Task<CopilotRunState?>? stop = null;
                System.Windows.Forms.ToolStripMenuItem[] items = [];
                try
                {
                    await f.Transport.HistoryEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    Check(recovered.Get(run.RunId) is { IsActive: false, MayBeRunning: false } current &&
                        current.Status == terminal, "Fixture did not publish terminal state before pending history");
                    items = MainWindow.CopilotStopMenuItems([run],
                        selected => stop = recovered.CancelAsync(selected.Key, selected.RunId));
                    Check(items.Length == 1 && items[0].Enabled, "Captured native menu lost its exact active target");
                    items[0].PerformClick();
                    var final = await (stop ?? throw new Exception("Captured Stop did not call cancellation"))
                        .WaitAsync(TimeSpan.FromSeconds(3)) ?? throw new Exception("Stop returned no recovered run");
                    Check(final is { Status: "cancelled", MayBeRunning: false, WorktreeRetained: true } &&
                        final.ExitCode == code && final.Error?.Contains("logs") == true &&
                        final.Output.Contains("FAKE-READY") && f.Transport.HistoryReaders.Count == 1 &&
                        f.Transport.Launches == 1 && f.Transport.Stops == 0 && f.Transport.Cleanups == cleanups &&
                        f.Store.Load().Single().Saved!.TaskConfirmedStopped,
                        "Terminal history Stop lost real outcome, partial logs/work or owned inactive proof");
                    Check(final.OutputOffset == saved.State.OutputOffset + f.Transport.RecordedOutput.Length &&
                        final.Output.IndexOf("FAKE-READY", StringComparison.Ordinal) ==
                            final.Output.LastIndexOf("FAKE-READY", StringComparison.Ordinal),
                        "Terminal history Stop duplicated its partial replay or changed the accepted offset");
                    foreach (var reader in f.Transport.HistoryReaders) reader("LATE-HISTORY-REPLY\n");
                    Check(recovered.Get(run.RunId)!.Output == final.Output,
                        "Abandoned terminal history changed stopped output");
                    await recovered.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                }
                finally
                {
                    f.Transport.ReleaseHistory.TrySetResult();
                    foreach (var item in items) item.Dispose();
                    await recovery.WaitAsync(TimeSpan.FromSeconds(3));
                    if (stop is not null) await stop.WaitAsync(TimeSpan.FromSeconds(3));
                }
            }
        });
        await test("Copilot recovery history cannot delay verified Stop or change inactive state", async () =>
        {
            foreach (var (historyFirst, failure, complete, longHistory) in new[]
                { (false, false, false, false), (true, false, false, false), (false, true, false, false),
                    (true, false, true, false), (true, false, false, true) })
            {
                using var f = new Fixture();
                await using var first = f.Service();
                var run = await first.StartAsync(Request());
                await Wait(first, run.RunId, s => s.Output.Contains("FAKE-READY"));
                if (longHistory) f.Transport.Emit(new string('x', 128 * 1024 + 1024) + "\nFAKE-TAIL\n");
                f.Transport.End(1);
                await Wait(first, run.RunId, s => s.Status == "failed");
                await first.DisposeAsync();
                var saved = f.Store.Load().Single().Saved!;
                f.Store.Write(saved with { State = saved.State with { Status = "needs-attention", ResumeAttempts = 1 } });
                f.Transport.BlockPreflight = !historyFirst;
                f.Transport.BlockHistory = !(failure || complete);
                if (failure) f.Transport.Failure = "history";
                await using var recovered = f.Service();
                var recovery = recovered.RecoverAsync();
                if (complete) await recovery.WaitAsync(TimeSpan.FromSeconds(3));
                else await (historyFirst ? f.Transport.HistoryEntered.Task : f.Transport.RecoveryEntered.Task)
                        .WaitAsync(TimeSpan.FromSeconds(3));
                if (longHistory) Check(recovered.Get(run.RunId)!.Output.StartsWith("[Earlier output truncated]", StringComparison.Ordinal),
                    "Long-history fixture did not publish its clamped output before Stop");
                var stop = recovered.CancelAsync(run.Key, run.RunId);
                try
                {
                    var final = await stop.WaitAsync(TimeSpan.FromSeconds(3))
                        ?? throw new Exception("Stop returned no matching recovered run");
                    Check(final is { Status: "cancelled", MayBeRunning: false, ExitCode: 1, WorktreeRetained: true } &&
                        f.Transport.Launches == 1 && f.Transport.Stops == 0 &&
                        f.Transport.Cleanups == 0 && f.Transport.RecordedOutput.Contains("FAKE-READY") &&
                        f.Store.Load().Single().Saved!.TaskConfirmedStopped,
                        "Unavailable recovery history lost verified stop, real exit, logs/work or explicit warning");
                    Check(complete ? final.Error is null : final.Error?.Contains("logs") == true,
                        "Incomplete replay lost its logs warning or completed replay gained a false warning");
                    if (!failure)
                    {
                        var marker = longHistory ? "FAKE-TAIL" : "FAKE-READY";
                        Check(final.Output.Contains(marker) && f.Transport.HistoryReaders.Count == 1 &&
                            final.Output.IndexOf(marker, StringComparison.Ordinal) ==
                                final.Output.LastIndexOf(marker, StringComparison.Ordinal) &&
                            final.OutputOffset == saved.State.OutputOffset + f.Transport.RecordedOutput.Length,
                            "Recovery history was replayed twice or its preserved output offset changed");
                    }
                    var output = final.Output;
                    foreach (var reader in f.Transport.HistoryReaders) reader("LATE-HISTORY-REPLY\n");
                    Check(recovered.Get(run.RunId)!.Output == output, "Abandoned history changed stopped output");
                    await recovered.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                }
                finally
                {
                    f.Transport.ReleasePreflight.TrySetResult();
                    f.Transport.ReleaseHistory.TrySetResult();
                    await recovery.WaitAsync(TimeSpan.FromSeconds(3));
                    await stop.WaitAsync(TimeSpan.FromSeconds(3));
                }
            }
        });
        await test("Copilot confirmed-stopped needs-attention cancels blocked recovery without another task Stop", async () =>
        {
            using var f = new Fixture();
            await using var first = f.Service();
            var run = await first.StartAsync(Request());
            await Wait(first, run.RunId, s => s.Output.Contains("FAKE-READY"));
            f.Transport.End(1);
            await Wait(first, run.RunId, s => s.Status == "failed");
            await first.DisposeAsync();
            var saved = f.Store.Load().Single().Saved!;
            Check(saved.TaskConfirmedStopped && !saved.State.MayBeRunning, "Fixture did not persist a confirmed-stopped run");
            f.Store.Write(saved with { State = saved.State with { Status = "needs-attention" } });
            f.Transport.BlockPreflight = true;
            await using var recovered = f.Service();
            var recovery = recovered.RecoverAsync();
            await f.Transport.RecoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var stop = recovered.CancelAsync(run.Key, run.RunId);
            try
            {
                var final = await stop.WaitAsync(TimeSpan.FromSeconds(3));
                Check(final is { Status: "cancelled", MayBeRunning: false, WorktreeRetained: true, ExitCode: 1 } &&
                    final.Output.Contains("FAKE-READY") && f.Transport.Launches == 1 && f.Transport.Stops == 0 &&
                    f.Store.Load().Single().Saved!.TaskConfirmedStopped,
                    "Cancelling stopped recovery changed real exit history, stopped another task or lost durable ownership");
            }
            finally
            {
                f.Transport.ReleasePreflight.TrySetResult();
                await recovery.WaitAsync(TimeSpan.FromSeconds(3));
                await stop.WaitAsync(TimeSpan.FromSeconds(3));
            }
        });
        await test("Copilot private prompt cleanup cannot hold verified Stop behind a hung transport", async () =>
        {
            foreach (var hung in new[] { true, false })
            {
                using var f = new Fixture();
                await using var service = f.Service();
                var run = await service.StartAsync(Request());
                await Wait(service, run.RunId, s => s.Output.Contains("FAKE-READY"));
                f.Transport.BlockDiscard = hung;
                if (!hung) f.Transport.Failure = "discard-prompt";
                var stop = service.CancelAsync(run.Key, run.RunId);
                try
                {
                    if (hung) await f.Transport.DiscardEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    var final = await stop.WaitAsync(TimeSpan.FromSeconds(3));
                    Check(final!.Status == "cancelled" && !final.MayBeRunning && final.WorktreeRetained &&
                        final.Error!.Contains("prompt cleanup") && final.Output.Contains("FAKE-READY"),
                        "Verified stop lost truthful liveness, preserved work/output or explicit cleanup warning");
                    var call = f.Transport.Calls.Single(c => c.Operation == "discard-prompt");
                    Check(!call.Script.Contains("source ") && !call.Script.Contains("nvm"),
                        "Private prompt cleanup re-entered blocking shell initialization");
                }
                finally
                {
                    f.Transport.ReleaseDiscard.TrySetResult();
                    await stop.WaitAsync(TimeSpan.FromSeconds(3));
                }
            }
        });
        await test("Copilot task registry context stays captured across launch stop and recovery", async () =>
        {
            foreach (var stateOverride in new[] { "", "/home/offline/task registry 'override" })
            {
                using var f = new Fixture();
                f.Transport.RegistryConfig = "/home/offline/config 'registry";
                f.Transport.RegistryOverride = stateOverride;
                await using var service = f.Service();
                var run = await service.StartAsync(Request());
                await Wait(service, run.RunId, s => s.Output.Contains("FAKE-READY"));
                var saved = f.Store.Load().Single().Saved!;
                var environment = JsonSerializer.Serialize(saved.Environment);
                Check(environment.Contains("XdgConfigHome"),
                    "The launch registry context was not captured in the durable record");
                await service.DisposeAsync();
                f.Transport.BlockPreflight = stateOverride.Length == 0;
                if (!f.Transport.BlockPreflight) f.Transport.RegistryConfig = "/home/offline/changed-registry";
                await using var recovered = f.Service();
                var recovery = recovered.RecoverAsync();
                if (f.Transport.BlockPreflight)
                    await f.Transport.RecoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                else
                {
                    await recovery.WaitAsync(TimeSpan.FromSeconds(3));
                    Check(recovered.Get(run.RunId)!.Status == "needs-attention" && f.Transport.Launches == 1,
                        "Changed registry was adopted or automatically relaunched");
                }
                var stopped = await recovered.CancelAsync(run.Key, run.RunId).WaitAsync(TimeSpan.FromSeconds(3));
                await recovery.WaitAsync(TimeSpan.FromSeconds(3));
                Check(stopped!.Status == "cancelled" && !stopped.MayBeRunning && stopped.WorktreeRetained &&
                    stopped.Output.Contains("FAKE-READY"), "Captured-registry cancellation lost truthful state, output or work");
                foreach (var call in f.Transport.Calls.Where(c => c.Operation is "launch" or "follow" or "status" or "stop" or "history"))
                {
                    Check(call.Script.Contains("export XDG_CONFIG_HOME='/home/offline/config '\\''registry'") &&
                        call.Script.Contains(stateOverride.Length == 0 ? "unset BGTASK_STATE_DIR" :
                            "export BGTASK_STATE_DIR='/home/offline/task registry '\\''override'"),
                        "Launch and control did not use the same captured task registry");
                    if (call.Operation is "status" or "stop" or "follow" or "history")
                        Check(!call.Script.Contains("source ") && !call.Script.Contains("nvm"),
                            "Emergency task control re-entered potentially blocking environment setup");
                }
            }
        });
        await test("Copilot failed uncertain cancellation retains its cross-dashboard reservation and exact-run retry proves stop", async () =>
        {
            using var f = new Fixture();
            await using var service = f.Service();
            var run = await service.StartAsync(Request());
            await Wait(service, run.RunId, s => s.Output.Contains("FAKE-READY"));
            f.Transport.Failure = "stop";
            try { await service.CancelAsync(run.Key, run.RunId); } catch (InvalidOperationException) { }
            Check(service.Get(run.RunId)!.MayBeRunning && f.Store.Load().Single().Saved!.State.MayBeRunning &&
                !f.Store.Load().Single().Saved!.TaskConfirmedStopped, "Failed stop manufactured terminal proof");
            await using (var other = f.Service())
            {
                try { await other.StartAsync(Request()); throw new Exception("Failed-but-live record lost its durable reservation"); }
                catch (InvalidOperationException) { }
            }
            f.Transport.Failure = null;
            var stopped = await service.CancelAsync(run.Key, run.RunId);
            Check(stopped is { Status: "cancelled", MayBeRunning: false, WorktreeRetained: true } &&
                f.Store.Load().Single().Saved!.TaskConfirmedStopped &&
                stopped.Request.Prompt == run.Request.Prompt,
                "Exact-run retry did not prove stop or mutated the captured prompt/suffix");
        });
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

        foreach (var recovered in new[] { false, true })
            foreach (var fault in new[] { "held-lock", "ownership" })
                await test($"Copilot exited supervisor cannot prove stopped work without a free owned lock (recovered={recovered}, fault={fault})", async () =>
                {
                    using var f = new Fixture();
                    var first = f.Service();
                    var run = await first.StartAsync(Request());
                    await Wait(first, run.RunId, s => s.Output.Contains("FAKE-READY"));
                    var service = first;
                    if (recovered)
                    {
                        await first.DisposeAsync();
                        service = f.Service();
                        await service.RecoverAsync();
                        await Wait(service, run.RunId, s => s.Status == "running");
                    }
                    await using var active = service;
                    if (fault == "held-lock") f.Transport.LockHeld = true;
                    else f.Transport.Failure = "verify-run";
                    f.Transport.End(0);
                    var blocked = await Wait(active, run.RunId, s => s.Status == "needs-attention");
                    Check(blocked.MayBeRunning && blocked.FinishedAt is null && f.Transport.Cleanups == 0 &&
                        !f.Store.Load().Single().Saved!.TaskConfirmedStopped,
                        "Supervisor exit released liveness or cleaned work without owned process-group stop proof");
                    try { await active.StartAsync(Request()); throw new Exception("Uncertain descendants lost their PR reservation"); }
                    catch (InvalidOperationException) { }
                    if (recovered)
                    {
                        try { await active.CancelAsync(run.Key, run.RunId); throw new Exception("Unverified cancellation claimed success"); }
                        catch (InvalidOperationException) { }
                    }
                    try { await active.StopAllAsync(); throw new Exception("Shutdown claimed all descendants stopped"); }
                    catch (InvalidOperationException) { }
                    Check(active.Get(run.RunId)!.MayBeRunning && !f.Store.Load().Single().Saved!.TaskConfirmedStopped,
                        "Stop retry discarded the uncertain execution-lock proof");
                    f.Transport.LockHeld = false;
                    f.Transport.Failure = null;
                    if (recovered) await active.CancelAsync(run.Key, run.RunId);
                    else await active.StopAllAsync();
                    Check(active.Get(run.RunId)!.Status == "cancelled" && !active.Get(run.RunId)!.MayBeRunning &&
                        f.Store.Load().Single().Saved!.TaskConfirmedStopped && f.Transport.Cleanups == 0,
                        "A free owned execution lock did not permit verified cancellation while retaining work");
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
                var provenStopped = fault is "session" or "resume-used";
                Check(recovered.Get(run.RunId)!.MayBeRunning == !provenStopped &&
                    f.Store.Load().Single().Saved!.TaskConfirmedStopped == provenStopped,
                    "Verified stopped recovery kept a false running indicator or ambiguous liveness was marked stopped");
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
        await test("Copilot recovered cancellation preserves replayed output offsets while reconciling terminal saved state", async () =>
        {
            using var f = new Fixture();
            var first = f.Service();
            var run = await first.StartAsync(Request());
            await Wait(first, run.RunId, s => s.Output.Contains("FAKE-READY"));
            await first.DisposeAsync();
            f.Transport.End(143, outcome: false);
            await using var resumed = f.Service();
            await resumed.RecoverAsync();
            f.Transport.Emit("Synthetic continuation failed.\n");
            f.Transport.End(1);
            var rendered = await Wait(resumed, run.RunId, s => s.Status == "needs-attention" &&
                s.OutputOffset > f.Store.Load().Single().Saved!.State.OutputOffset);
            var saved = f.Store.Load().Single().Saved!;
            f.Store.Write(saved with { State = saved.State with { Status = "cancelled", FinishedAt = DateTimeOffset.UtcNow } });
            var cancelled = await resumed.CancelAsync(run.Key, run.RunId);
            Check(cancelled!.Status == "cancelled" && cancelled.Output == rendered.Output &&
                cancelled.OutputOffset == rendered.OutputOffset && cancelled.WorktreeRetained,
                "Saved-state reconciliation regressed the rendered UTF-16 offset and would discard a valid cancellation in the UI");
        });
        await test("Copilot cancellation before recovered follow preserves intentional history replay", async () =>
        {
            foreach (var status in new[] { "running", "needs-attention" })
            {
                using var f = new Fixture();
                var first = f.Service();
                var run = await first.StartAsync(Request());
                await Wait(first, run.RunId, state => state.Output.Contains("FAKE-READY"));
                f.Transport.Emit("SUPERVISED HISTORY\n");
                await first.DisposeAsync();
                var saved = f.Store.Load().Single().Saved!;
                f.Store.Write(saved with { State = saved.State with { Status = status, MayBeRunning = true } });
                f.Transport.BlockPreflight = true;
                await using var recovered = f.Service();
                var recovery = recovered.RecoverAsync();
                try
                {
                    await f.Transport.RecoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    var stopped = await recovered.CancelAsync(run.Key, run.RunId).WaitAsync(TimeSpan.FromSeconds(3));
                    Check(stopped is { Status: "cancelled", MayBeRunning: false, WorktreeRetained: true } &&
                        stopped.Output.Contains("SUPERVISED HISTORY") && f.Transport.Launches == 1 && f.Transport.Stops == 1,
                        "Cancelling before follow attachment discarded owned supervised history or relaunched");
                    Check(f.Transport.Calls.Where(c => c.Operation is "verify-run" or "history")
                        .All(c => !c.Script.Contains("source ") && !c.Script.Contains("nvm")),
                        "Legacy cancellation re-entered shell setup after owned stop verification");
                }
                finally
                {
                    f.Transport.ReleasePreflight.TrySetResult();
                    await recovery.WaitAsync(TimeSpan.FromSeconds(3));
                }
            }
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

    internal sealed class Fixture : IDisposable
    {
        public readonly string Directory = Path.Combine(Environment.CurrentDirectory, ".copilot-recovery-fixture-" + Guid.NewGuid().ToString("N"));
        public readonly Fake Transport = new();
        public CopilotRunService.DurableStore Store => new(Directory);
        public CopilotRunService Service() => new(Transport, Directory);
        public string Record(string id) => Path.Combine(Directory, id + ".json");
        public void Dispose() { if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true); }
    }

    internal sealed class Fake : ICopilotRunTransport
    {
        readonly object gate = new();
        public readonly ConcurrentQueue<CopilotProcessRequest> Calls = new();
        public readonly ConcurrentQueue<Action<string>> HistoryReaders = new();
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
        public bool BlockPreflight, BlockFollowers, BlockDiscard, BlockHistory, BlockCleanup;
        public string RecordedOutput { get { lock (gate) return log; } }
        public string? RegistryConfig;
        public string RegistryOverride = "";
        public readonly TaskCompletionSource RecoveryEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ReleasePreflight = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ReleaseFollowers = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource DiscardEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ReleaseDiscard = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource HistoryEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ReleaseHistory = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource CleanupEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ReleaseCleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource CleanupReturned = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
            if (r.Operation == "discard-prompt" && BlockDiscard)
            {
                DiscardEntered.TrySetResult();
                await ReleaseDiscard.Task;
                return new(0, "", "");
            }
            string Protocol(params string[] values) => "ADOPRVIEW1\0" + string.Join('\0', values) + "\0";
            switch (r.Operation)
            {
                case "preflight":
                    if (BlockPreflight)
                    {
                        RecoveryEntered.TrySetResult();
                        await ReleasePreflight.Task.WaitAsync(token);
                    }
                    return new(0, Protocol("Offline-Ubuntu", "offline", "/home/offline", "/home/offline/source",
                    "/home/offline/source/.git", "/home/offline/bin/copilot", "/home/offline/bin/bgtask", Help, "/home/offline/.copilot",
                    "origin", "https://dev.azure.com/offline-org/offline-project/_git/offline-repo",
                    "https://dev.azure.com/offline-org/offline-project/_git/offline-repo", new string('a', 64)) +
                    (RegistryConfig is null ? "" : "ADOPRVIEW_TASK_REGISTRY\0" + RegistryConfig + "\0" + RegistryOverride + "\0END_TASK_REGISTRY\0"), "");
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
                    try
                    {
                        if (BlockFollowers) await ReleaseFollowers.Task;
                        else await done.WaitAsync(token);
                        return new(0, "", "");
                    }
                    finally { lock (gate) { if (stdout is not null) readers.Remove(stdout); } }
                case "history":
                    if (stdout is not null) HistoryReaders.Enqueue(stdout);
                    stdout?.Invoke(log);
                    if (BlockHistory)
                    {
                        HistoryEntered.TrySetResult();
                        await ReleaseHistory.Task;
                    }
                    return new(0, log, "");
                case "stop": Stops++; End(143); return new(0, "", "");
                case "cleanup":
                    Check(ended.Task.IsCompleted, "Cleanup attempted on a live fake session");
                    Cleanups++;
                    if (BlockCleanup)
                    {
                        CleanupEntered.TrySetResult();
                        await ReleaseCleanup.Task;
                        CleanupReturned.TrySetResult();
                        return new(0, Protocol("REMOVED"), "");
                    }
                    if (Cleanup == "REMOVED") WorkspaceRemoved = true;
                    return new(0, Protocol(Cleanup, "fake retained work"), "");
                default: return new(0, "", "");
            }
        }
    }
}
