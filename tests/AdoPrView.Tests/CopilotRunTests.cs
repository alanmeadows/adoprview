using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using AdoPrView;

static class CopilotRunTests
{
    const string Commit = "1111111111111111111111111111111111111111";
    const string Help = "--no-ask-user --stream --no-color --deny-tool --yolo --session-id --resume";

    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    static CopilotRunRequest Request(int id = 1, string prompt = "Fix only this selected PR") =>
        new($"repo-id:{id}", id, $"PR {id}", "offline-org", "offline-project", "offline-repo", "repo-id",
            "refs/heads/fix-test", Commit, prompt, "Offline-Ubuntu", "/home/offline/clones/offline-repo");

    static async Task<CopilotRunState> Wait(CopilotRunService service, string id, Func<CopilotRunState, bool> predicate)
    {
        var result = new TaskCompletionSource<CopilotRunState>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(CopilotRunState s)
        {
            if (s.RunId != id) return;
            if (predicate(s)) result.TrySetResult(s);
            else if (!s.IsActive && !(s.Status == "completed" && s.WorktreeStatus is "created" or "in-use"))
                result.TrySetException(new Exception($"Offline run {s.Title} ended {s.Status}: {s.Message}; {s.Output}"));
        }
        service.Changed += Changed;
        try
        {
            if (service.Get(id) is { } current) Changed(current);
            return await result.Task.WaitAsync(TimeSpan.FromSeconds(60));
        }
        finally { service.Changed -= Changed; }
    }

    static async Task ExpectError(Func<Task> action, string message)
    {
        try { await action(); }
        catch (InvalidOperationException ex)
        {
            Check(ex.Message.Contains(message, StringComparison.OrdinalIgnoreCase), "Wrong failure: " + ex.Message);
            return;
        }
        throw new Exception("Expected an explicit failure containing " + message);
    }

    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("Copilot native Stop notice retains returned warnings without inventing stop proof", () =>
        {
            var stopped = new CopilotRunState(new string('a', 32), Request(), "cancelled", "", 143, null,
                "Ubuntu", "/source", "/work", "retained", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            foreach (var error in new[] { "Private prompt cleanup could not be confirmed; the private file may remain.",
                "Saved task logs could not be fully replayed after Stop." })
            {
                var notice = MainWindow.CopilotStopNotice(stopped with { Error = error });
                Check(notice.Message.Contains(error) && notice.Message.Contains("confirmed inactive") &&
                    notice.Message.Contains("worktree is retained") && notice.Image == MessageBoxImage.Warning,
                    "Native Stop notice hid a returned cleanup/history warning");
            }
            var clean = MainWindow.CopilotStopNotice(stopped with { Error = " \r\n" });
            Check(clean.Image == MessageBoxImage.Information && clean.Message.Contains("confirmed inactive"),
                "Clean confirmed stop gained a false warning");
            var uncertain = MainWindow.CopilotStopNotice(stopped with { MayBeRunning = true, Error = "Execution lock remains held." });
            Check(uncertain.Message.Contains("Stop could not be confirmed") &&
                uncertain.Message.Contains("Execution lock remains held.") && uncertain.Image == MessageBoxImage.Warning,
                "Uncertain native Stop lost its diagnostic or fabricated inactive proof");
            Check(MainWindow.CopilotStopNotice(null).Image == MessageBoxImage.Warning, "Missing run fabricated stop proof");
            return Task.CompletedTask;
        });
        await test("Copilot native menu excludes unreadable records and binds distinguishable exact run identities", () =>
        {
            var a = new CopilotRunState(new string('a', 32), Request(), "needs-attention", "", null, null,
                "Ubuntu", "/source", "/work", "retained", DateTimeOffset.UtcNow, null);
            var b = a with { RunId = new string('b', 32), Request = a.Request with { Key = "other:123" }, MayBeRunning = true };
            var unreadable = a with { RunId = new string('c', 32), Request = a.Request with { Id = 0 } };
            var stopped = new List<(string, string)>();
            var items = MainWindow.CopilotStopMenuItems([a, b, unreadable, a with { Status = "completed" }],
                run => stopped.Add((run.Key, run.RunId)));
            try
            {
                Check(items.Length == 2 && items[0].Text != items[1].Text &&
                    items[0].Text?.Contains(a.RunId) == true && items[1].Text?.Contains(b.RunId) == true,
                    "Native menu included a non-actionable record or indistinguishable cancellation targets");
                items[1].PerformClick();
                items[0].PerformClick();
                Check(stopped.SequenceEqual(new[] { (b.Key, b.RunId), (a.Key, a.RunId) }),
                    "Native menu click did not preserve its selected key/run identity");
            }
            finally { foreach (var item in items) item.Dispose(); }
            var empty = MainWindow.CopilotStopMenuItems([unreadable], _ => throw new Exception("Placeholder was actionable"));
            try { Check(empty.Length == 1 && !empty[0].Enabled, "Empty menu exposed a cancellation target"); }
            finally { foreach (var item in empty) item.Dispose(); }
            return Task.CompletedTask;
        });
        await test("Copilot short saved output allocates proportional tail storage without losing growth or wrap", () =>
        {
            _ = new CopilotRunService.OutputTail("");
            var tails = new CopilotRunService.OutputTail[8];
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < tails.Length; i++)
            {
                tails[i] = new(i % 2 == 0 ? "" : "saved-log\n");
                tails[i].Append("retained-tail\n");
            }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Console.WriteLine($"TAIL-MEMORY savedRuns={tails.Length} allocatedBytes={allocated} limitBytes={64 * 1024}");
            Check(allocated < 64 * 1024, $"Eight empty/short saved runs allocated {allocated} bytes of tail storage");
            for (var i = 0; i < tails.Length; i++)
                Check(tails[i].ToString() == (i % 2 == 0 ? "" : "saved-log\n") + "retained-tail\n" &&
                    !tails[i].Truncated, "Small tail growth truncated or changed saved output");
            tails[0].Append(new string('x', CopilotRunService.MaxOutputCharacters));
            tails[0].Append("WRAPPED-END");
            Check(tails[0].Truncated && tails[0].ToString().Length == CopilotRunService.MaxOutputCharacters &&
                tails[0].ToString().EndsWith("WRAPPED-END"), "Adaptive storage did not preserve the bounded ring tail");
            return Task.CompletedTask;
        });
        await test("Copilot bounded output avoids snapshot allocation per chunk and hung follow cannot block exact cancel or detach", async () =>
        {
            foreach (var detach in new[] { false, true })
            {
                var fake = new FakeTransport { IgnoreFollowCancellation = true };
                var service = new CopilotRunService(fake);
                var run = await service.StartAsync(Request());
                await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var chunk = new string('x', 4096);
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < 64; i++) fake.Output(chunk);
                var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Check(allocated < 2 * 1024 * 1024, $"Output allocated {allocated} bytes for a bounded 256 KiB fixture");
                fake.Output("\nRETAINED-TAIL\n");
                var tail = service.Get(run.RunId)!.Output;
                Check(tail.Length <= CopilotRunService.MaxOutputCharacters && tail.EndsWith("RETAINED-TAIL\n"),
                    "Bounded snapshots lost tail output");
                if (detach)
                {
                    await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                    Check(service.Get(run.RunId)!.Status == "running" && !fake.Calls.Any(c => c.Operation == "stop"),
                        "Detachment stopped or completed a supervised job");
                }
                else
                {
                    var stopped = await service.CancelAsync(run.Key, run.RunId).WaitAsync(TimeSpan.FromSeconds(3));
                    Check(stopped!.Status == "cancelled" && !stopped.MayBeRunning && stopped.WorktreeRetained &&
                        stopped.Output.EndsWith("RETAINED-TAIL\n") && fake.Calls.Count(c => c.Operation == "stop") == 1,
                        "Cancellation lost work/output or claimed an unconfirmed outcome");
                    await service.DisposeAsync();
                }
                fake.Output("LATE OUTPUT");
                Check(service.Get(run.RunId)!.Output == tail, "Abandoned transport mutated detached/terminal output");
                fake.Finish(143);
            }
        });
        await test("Copilot browser delivery coalesces output behind one acknowledgement and disposal releases hung delivery", async () =>
        {
            var posted = new ConcurrentQueue<(CopilotRunState State, long Id)>();
            await using var delivery = new CopilotRunDelivery((state, id) =>
            { posted.Enqueue((state, id)); return Task.CompletedTask; }, ex => throw new Exception("Delivery failed", ex), waitForReady: true);
            var initial = new CopilotRunState("offline", Request(), "running", "initial", null, null,
                null, null, "/offline/worktree", "in-use", DateTimeOffset.UtcNow, null);
            delivery.Offer(initial);
            await Task.Delay(50);
            Check(posted.IsEmpty, "Output was sent before the dashboard registered its receiver");
            delivery.Ready();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (posted.IsEmpty) await Task.Delay(10, deadline.Token);
            for (var i = 0; i < 64; i++) delivery.Offer(initial with { Output = i.ToString() });
            delivery.Offer(initial with { Status = "cancelled", Output = "final", MayBeRunning = false });
            await Task.Delay(150);
            Check(posted.Count == 1, "Busy browser accumulated messages");
            delivery.Acknowledge(posted.First().Id - 1);
            Check(posted.Count == 1, "Stale acknowledgement released another delivery");
            // The previous browser disappeared without acknowledging; fresh ready/init must recover the terminal state.
            delivery.Ready();
            while (posted.Count < 2) await Task.Delay(10, deadline.Token);
            Check(posted.Last().State.Status == "cancelled" && posted.Last().State.Output == "final",
                "Coalescing discarded the latest terminal output");
            delivery.Offer(initial with { Status = "cancelled", Output = "final worktree status" });
            delivery.Acknowledge(posted.First().Id);
            await Task.Delay(100);
            Check(posted.Count == 2, "A previous browser's stale acknowledgement released a newer snapshot");
            delivery.Acknowledge(posted.Last().Id);
            while (posted.Count < 3) await Task.Delay(10, deadline.Token);
            Check(posted.Last().State.Output == "final worktree status", "Later terminal cleanup/status update was lost");
            await delivery.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        });
        await test("Copilot restored delivery reports later failure only after a successful resynchronization post", async () =>
        {
            var failures = new HashSet<string>(StringComparer.Ordinal);
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var repeated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var later = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var success = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            var attempts = 0; var reports = 0; var notifications = 0;
            await using var delivery = MainWindow.CreateCopilotDelivery((_, id) =>
            {
                if (Interlocked.Increment(ref attempts) != 3) throw new IOException("synthetic disconnected dashboard");
                success.TrySetResult(id);
                return Task.CompletedTask;
            }, _ =>
            {
                lock (failures)
                    if (failures.Add("copilot-state-delivery")) notifications++;
                switch (Interlocked.Increment(ref reports))
                {
                    case 1: first.TrySetResult(); break;
                    case 2: repeated.TrySetResult(); break;
                    case 3: later.TrySetResult(); break;
                }
            }, failures);
            var state = new CopilotRunState("offline", Request(), "running", "retained", null, null,
                null, null, "/offline/work", "in-use", DateTimeOffset.UtcNow, null) { Revision = 1 };
            delivery.Offer(state);
            await first.Task.WaitAsync(TimeSpan.FromSeconds(3));
            delivery.Ready();
            await repeated.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(notifications == 1, "Repeated failure without successful recovery generated duplicate notifications");
            delivery.Ready();
            var id = await success.Task.WaitAsync(TimeSpan.FromSeconds(3));
            delivery.Acknowledge(id);
            delivery.Offer(state with { Revision = 2, Status = "completed" });
            await later.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(notifications == 2, "A successful resynchronization left later delivery failure silently suppressed");
            foreach (var failLate in new[] { false, true })
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var abandonedReports = 0;
                await using var abandoned = MainWindow.CreateCopilotDelivery((_, _) =>
                {
                    entered.TrySetResult();
                    return release.Task;
                }, _ => Interlocked.Increment(ref abandonedReports), failures);
                abandoned.Offer(state);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                await abandoned.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
                if (failLate) release.TrySetException(new InvalidOperationException("synthetic disposed post failure"));
                else release.TrySetResult();
                await Task.Delay(50);
                lock (failures)
                    Check(failures.Contains("copilot-state-delivery") && abandonedReports == 0,
                        "An abandoned late post cleared the current marker or reported after completed disposal");
            }
        });
        await test("Copilot restored replay cannot replace newer pending lifecycle or output snapshots", async () =>
        {
            var initial = new CopilotRunState("offline", Request(), "running", "initial", null, null,
                null, null, "/offline/worktree", "in-use", DateTimeOffset.UtcNow, null)
                { Revision = 2, OutputOffset = 100 };
            foreach (var outputOnly in new[] { false, true })
            {
                var newer = outputOnly
                    ? initial with { Output = "newer output", OutputOffset = 200 }
                    : initial with { Revision = 3, Status = "completed", WorktreeStatus = "removed-clean" };
                var posted = new ConcurrentQueue<(CopilotRunState State, long Id)>();
                await using var delivery = new CopilotRunDelivery((state, id) =>
                { posted.Enqueue((state, id)); return Task.CompletedTask; }, ex => throw ex, waitForReady: true);
                delivery.Offer(newer);
                delivery.Offer(initial);
                delivery.Ready();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                while (posted.IsEmpty) await Task.Delay(10, deadline.Token);
                Check(posted.First().State == newer, "Restored replay permanently discarded newer pending lifecycle/output state");
                delivery.Offer(initial);
                delivery.Ready();
                while (posted.Count < 2) await Task.Delay(10, deadline.Token);
                Check(posted.Last().State == newer, "Older replay superseded the in-flight newest snapshot");
            }
        });
        await test("Copilot coalesced delivery disposal does not wait for hung posting transport", async () =>
        {
            foreach (var synchronous in new[] { false, true })
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var delivery = new CopilotRunDelivery((_, _) =>
                {
                    entered.TrySetResult();
                    if (!synchronous) return release.Task;
                    try { release.Task.GetAwaiter().GetResult(); throw new InvalidOperationException("synthetic late post failure"); }
                    finally { finished.TrySetResult(); }
                }, ex => throw ex);
                try
                {
                    delivery.Offer(new("offline", Request(), "running", "retained output", null, null,
                        null, null, "/offline/worktree", "in-use", DateTimeOffset.UtcNow, null));
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                    await delivery.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
                }
                finally
                {
                    release.TrySetResult();
                    if (synchronous) await finished.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    await delivery.DisposeAsync();
                }
            }
        });
        await test("Copilot abandoned startup persists a terminal reservation before releasing its lease", async () =>
        {
            using var f = new CopilotRecoveryTests.Fixture();
            await using var service = f.Service();
            var acquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            service.BeforeRunRegistration = () =>
            {
                acquired.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("synthetic reservation hold");
            };
            var starting = service.StartAsync(Request());
            try
            {
                await acquired.Task.WaitAsync(TimeSpan.FromSeconds(3));
                await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            }
            finally { release.Set(); }
            try { await starting; throw new Exception("Shutdown admitted a late run"); }
            catch (ObjectDisposedException) { }
            var saved = f.Store.Load().Single().Saved!;
            Check(saved.State.Status == "cancelled" && !saved.LaunchIntent && !saved.State.MayBeRunning &&
                f.Transport.Launches == 0, "Abandoned startup retained a false live reservation");
            await using var next = f.Service();
            var run = await next.StartAsync(Request());
            await Wait(next, run.RunId, state => state.Output.Contains("FAKE-READY"));
            await next.CancelAsync(run.Key, run.RunId);
        });
        await test("Copilot exact-run controls survive held and failed production shutdown sequencing", async () =>
        {
            using var fake = new FakeTransport();
            await using var service = new CopilotRunService(fake);
            var run = await service.StartAsync(Request());
            await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var shutdown = MainWindow.DisposeCopilotAfterAsync(service, () => release.Task);
            try
            {
                var stopped = await service.CancelAsync(run.Key, run.RunId).WaitAsync(TimeSpan.FromSeconds(2));
                Check(stopped is { Status: "cancelled", MayBeRunning: false } && !shutdown.IsCompleted,
                    "Held teardown disabled exact-run Stop");
            }
            finally { release.TrySetException(new InvalidOperationException("synthetic teardown failure")); }
            try { await shutdown; throw new Exception("Failed teardown reported success"); }
            catch (InvalidOperationException ex) when (ex.Message == "synthetic teardown failure") { }
            fake.Reset();
            var next = await service.StartAsync(Request(2));
            await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await service.CancelAsync(next.Key, next.RunId);
        });
        await test("Copilot lifecycle revisions distinguish same-output terminal cleanup snapshots", async () =>
        {
            using var fake = new FakeTransport { BlockCleanup = true };
            await using var service = new CopilotRunService(fake);
            var run = await service.StartAsync(Request());
            await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            fake.Output("retained synthetic output\n");
            fake.Finish(0);
            try
            {
                await fake.CleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var retained = service.Get(run.RunId)!;
                fake.CleanupReleased.TrySetResult();
                var removed = await Wait(service, run.RunId, state => state.WorktreeStatus == "removed-clean");
                Check(retained.Status == "completed" && retained.WorktreeRetained &&
                    removed.OutputOffset == retained.OutputOffset && removed.Revision > retained.Revision,
                    "Same-output cleanup lacked a strictly newer lifecycle revision");
                var projected = JsonSerializer.SerializeToElement(MainWindow.RunForUi(removed), Bridge.Json);
                Check(projected.TryGetProperty("revision", out var revision) && revision.GetInt64() == removed.Revision &&
                    projected.GetProperty("outputOffset").GetInt64() == removed.OutputOffset,
                    "Serialized native init/action/delivery projection dropped the lifecycle revision or output offset");
            }
            finally { fake.CleanupReleased.TrySetResult(); }
        });
        await test("Copilot browser delivery fairly publishes a second run while the first keeps updating", async () =>
        {
            var posted = new ConcurrentQueue<(CopilotRunState State, long Id)>();
            await using var delivery = new CopilotRunDelivery((state, id) =>
            { posted.Enqueue((state, id)); return Task.CompletedTask; }, ex => throw ex);
            var first = new CopilotRunState("first", Request(), "running", "A1", null, null,
                null, null, "/offline/worktree", "in-use", DateTimeOffset.UtcNow, null);
            var second = first with { RunId = "second", Request = Request(2), Status = "cancelled", Output = "B final" };
            delivery.Offer(first);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (posted.Count < 1) await Task.Delay(10, deadline.Token);
            delivery.Offer(first with { Output = "A2" });
            delivery.Offer(second);
            delivery.Acknowledge(posted.Last().Id);
            while (posted.Count < 2) await Task.Delay(10, deadline.Token);
            delivery.Offer(first with { Output = "A3" });
            delivery.Acknowledge(posted.Last().Id);
            while (posted.Count < 3) await Task.Delay(10, deadline.Token);
            Check(posted.Last().State == second, "Streaming first run starved the second run's terminal state");
        });
        await test("Copilot restored browser delivery rejects acknowledgements from its prior instance", async () =>
        {
            var posted = new ConcurrentQueue<(CopilotRunState State, long Id)>();
            Task Post(CopilotRunState state, long id) { posted.Enqueue((state, id)); return Task.CompletedTask; }
            var state = new CopilotRunState("restored", Request(), "running", "retained", null, null,
                null, null, "/offline/worktree", "in-use", DateTimeOffset.UtcNow, null);
            await using var previous = new CopilotRunDelivery(Post, ex => throw ex);
            previous.Offer(state);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (posted.Count < 1) await Task.Delay(10, deadline.Token);
            var oldId = posted.Last().Id;
            await previous.DisposeAsync();
            await using var restored = new CopilotRunDelivery(Post, ex => throw ex);
            restored.Offer(state);
            while (posted.Count < 2) await Task.Delay(10, deadline.Token);
            restored.Offer(state with { Status = "cancelled" });
            restored.Acknowledge(oldId);
            await Task.Delay(50, deadline.Token);
            Check(posted.Count == 2, "A pre-shutdown acknowledgement released the restored delivery");
            restored.Acknowledge(posted.Last().Id);
            while (posted.Count < 3) await Task.Delay(10, deadline.Token);
        });
        await test("Copilot response deadline bypasses blocking and throwing transport cancellation callbacks", async () =>
        {
            foreach (var throwing in new[] { false, true })
            {
                using var fake = new FakeTransport { HungOperation = "status", ThrowCancellation = throwing };
                await using var service = new CopilotRunService(fake);
                var output = new StringBuilder();
                try
                {
                    var timing = Stopwatch.StartNew();
                    var control = service.Invoke("status", "Offline-Ubuntu", "", [],
                        stdout: text => output.Append(text), responseLimit: TimeSpan.FromMilliseconds(100));
                    await fake.ControlEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    try { await control.WaitAsync(TimeSpan.FromSeconds(2)); throw new Exception("Hung transport reported success"); }
                    catch (Exception ex) when (ex.Message.Contains("response limit") && ex.Message.Contains("unconfirmed")) { }
                    Check(timing.Elapsed < TimeSpan.FromSeconds(1), "Transport callback held the independent deadline");
                    await fake.CancellationEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    fake.Output("ABANDONED OUTPUT");
                    Check(output.Length == 0, "Abandoned control emitted late output");
                }
                finally
                {
                    fake.ReleaseCancellation.Set();
                    await fake.CancellationFinished.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    fake.ControlReleased.TrySetResult();
                }
            }
        });
        await test("Copilot cancellation and detach bypass a blocked transport cancellation callback", async () =>
        {
            foreach (var detach in new[] { false, true })
            {
                using var fake = new FakeTransport { BlockCancellation = true };
                var service = new CopilotRunService(fake);
                try
                {
                    var run = await service.StartAsync(Request());
                    await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    fake.Output("KEEP THIS OUTPUT\n");
                    var control = detach ? service.DisposeAsync().AsTask() : service.CancelAsync(run.Key, run.RunId);
                    await fake.CancellationEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    await control.WaitAsync(TimeSpan.FromSeconds(3));
                    if (!detach)
                    {
                        var state = service.Get(run.RunId);
                        Check(state is { Status: "cancelled", MayBeRunning: false } && state.Output.EndsWith("KEEP THIS OUTPUT\n"),
                            "Callback contention lost output or bypassed owned stop verification");
                    }
                }
                finally
                {
                    fake.ReleaseCancellation.Set();
                    fake.Finish(143);
                    if (fake.CancellationEntered.Task.IsCompleted)
                        await fake.CancellationFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    await service.DisposeAsync();
                }
            }
        });
        await test("Copilot returns preparing immediately, captures immutable identity/prompt and streams both channels", async () =>
        {
            var fake = new FakeTransport();
            await using var service = new CopilotRunService(fake);
            var prompt = "exact 'quoted' \"prompt\"\n$(never-execute)\r\nfinal newline\n";
            var request = Request(prompt: prompt) with { Title = "untrusted $(title)", Directory = "/home/offline/repo ' $(path)" };
            var initial = await service.StartAsync(request);
            Check(initial.Status == "preparing" && initial.RunId.Length == 32, "Start blocked or did not return preparing");
            request = request with { Prompt = "replacement", Title = "replacement" };
            await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(service.Get(initial.RunId)!.Request.Prompt == prompt && service.Get(initial.RunId)!.Title != request.Title, "Clicked identity mutated");
            var stage = fake.Calls.Single(c => c.Operation == "stage-prompt");
            Check(stage.StandardInput == prompt + CopilotRunService.PublishInstructions(service.Get(initial.RunId)!.Request, "origin", FakeTransport.PushHash),
                "Prompt was normalized, quoted or truncated");
            Check(fake.Calls.All(c => !c.Script.Contains(prompt) && !c.Arguments.Any(a => a.Contains(prompt))), "Prompt leaked into shell text/argv");
            var preflight = fake.Calls.Single(c => c.Operation == "preflight");
            Check(preflight.Arguments[0] == "/home/offline/repo ' $(path)" && !preflight.Script.Contains("$(path)"), "Path was interpolated");
            fake.Output("stdout \u001b[31mred");
            fake.Output("\u001b[0m\n");
            fake.Error("stderr \u001b]0;hostile-title");
            fake.Error("\aafter\n");
            var streaming = await Wait(service, initial.RunId, s => s.Output.Contains("stderr after"));
            Check(streaming.Output.Contains("stdout red") && !streaming.Output.Contains('\u001b') && !streaming.Output.Contains("hostile-title"), "Terminal controls survived");
            fake.Finish(7);
            var done = await Wait(service, initial.RunId, s => !s.IsActive);
            Check(done.Status == "failed" && done.ExitCode == 7 && done.WorktreeRetained, "Actual exit code/failure/retention missing");
            Check(done.Key == initial.Key && done.Id == 1 && done.Request.RepoId == "repo-id" && service.Runs.Count == 1, "Removed-row state lost identity");
        });

        foreach (var push in new[] { true, false })
            await test($"Copilot generated gates plus exact verified publish target reach runner stdin with matching launch authority (push={push})", async () =>
            {
                var prompt = await CopilotSourceTests.CreatePrompt();
                var fake = new FakeTransport();
                await using var service = new CopilotRunService(fake);
                var request = Request(prompt: prompt) with { SourceRef = "refs/heads/users/it's/fix", PushToSource = push };
                var run = await service.StartAsync(request);
                await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var delivered = fake.Calls.Single(c => c.Operation == "stage-prompt").StandardInput!;
                var prepare = fake.Calls.Single(c => c.Operation == "prepare");
                Check(delivered == prompt + CopilotRunService.PublishInstructions(request, "origin", FakeTransport.PushHash) && service.Get(run.RunId)!.Request.Prompt == prompt,
                    "Generated prompt was changed before runner stdin");
                CopilotSourceTests.CheckReviewGate(delivered);
                Check(push
                    ? delivered.Contains("publish only with: [ \"$(git remote get-url --push --all 'origin' | sha256sum)\" = '" + FakeTransport.PushHash + "  -' ] && " +
                        "git push --no-follow-tags --recurse-submodules=check 'origin' 'HEAD:refs/heads/users/it'\\''s/fix' and verify with: git ls-remote 'origin' 'refs/heads/users/it'\\''s/fix' plus a fresh GET of this PR. ") &&
                        delivered.EndsWith("The dashboard does not enforce this ref restriction; follow it exactly.\n") &&
                        delivered.Contains("its fetch URL and every effective push URL (git remote get-url --push --all, including pushurl and pushInsteadOf rewrites) point to this PR's repository. " +
                            "ADO reported source ref refs/heads/users/it's/fix at " + Commit) &&
                        prepare.Arguments[10] == FakeTransport.PushHash
                    : delivered.EndsWith("This run is not authorized to push (git push is denied). Report any commits as unpublished local work.\n") && !delivered.Contains("## Verified publish target") &&
                        prepare.Arguments[10] == "",
                    "Exact captured source ref/remote or no-push scope missing from staged prompt");
                var launch = fake.Calls.Single(c => c.Operation == "launch");
                Check(launch.Arguments[5] == CopilotRunService.RunnerScript.Replace("\r\n", "\n") && launch.Arguments[8] == (push ? "start-push" : "start") &&
                    CopilotRunService.RunnerScript.Contains("--yolo") && CopilotRunService.RunnerScript.Contains("<&3"),
                    "Generated prompt bypassed the existing permission/stdin launch path or lost its push authority");
                Check(service.Get(run.RunId)!.PermissionNotice == (push ? CopilotRunService.PushNotice : CopilotRunService.HeadlessNotice) &&
                    service.Get(run.RunId)!.Output.StartsWith(service.Get(run.RunId)!.PermissionNotice), "Visible permission notice misstates push authority");
                fake.Finish(0);
                await Wait(service, run.RunId, s => !s.IsActive);
            });

        await test("Copilot same-PR duplicate guard is native and stale cross-row stop controls are rejected", async () =>
        {
            var fake = new FakeTransport();
            await using var service = new CopilotRunService(fake);
            var first = await service.StartAsync(Request());
            await ExpectError(async () => await service.StartAsync(Request() with { Key = "alternate-row-key" }), "already active");
            await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await ExpectError(async () => await service.CancelAsync("another-row", first.RunId), "no longer matches");
            Check(!fake.Calls.Any(c => c.Operation == "stop"), "Stale UI control stopped an owned run");
            var stopped = await service.CancelAsync(first.Key, first.RunId);
            Check(stopped!.Status == "cancelled" && stopped.WorktreeRetained, "Cancellation erased the isolated worktree");
            var stops = fake.Calls.Where(c => c.Operation == "stop").ToArray();
            Check(stops.Length == 1 && stops[0].Arguments[1] == "ado-prview-" + first.RunId, "Stop was not bound to exact owned unique task");
            Check(!stops[0].Script.Contains("--all") && !stops[0].Script.Contains("--labels"), "Broad cancellation");
            fake.Reset();
            var second = await service.StartAsync(Request());
            await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fake.Finish(0);
            Check((await Wait(service, second.RunId, s => !s.IsActive)).Status == "completed", "Duplicate reservation did not release");
        });

        await test("Copilot distinct PRs from one source run concurrently in distinct exact-commit worktrees", async () =>
        {
            var fake = new FakeTransport();
            await using var service = new CopilotRunService(fake);
            var a = await service.StartAsync(Request(1));
            var b = await service.StartAsync(Request(2) with { SourceCommit = new string('2', 40) });
            await Wait(service, a.RunId, s => s.Status == "running");
            await Wait(service, b.RunId, s => s.Status == "running");
            Check(service.Get(a.RunId)!.WorktreePath != service.Get(b.RunId)!.WorktreePath, "Worktrees overlap");
            var prepared = fake.Calls.Where(c => c.Operation == "prepare").ToArray();
            Check(prepared.Length == 2 && prepared.Select(c => c.Arguments[5]).Order().SequenceEqual(new[] { Commit, new string('2', 40) }), "Wrong source commit");
            Check(prepared.All(c => c.Arguments[4] == "refs/heads/fix-test"), "Wrong source ref");
            fake.Finish(0);
            await Wait(service, a.RunId, s => !s.IsActive);
            await Wait(service, b.RunId, s => !s.IsActive);
        });

        await test("Copilot identity mapping rejects foreign and lookalike remotes before creating anything", async () =>
        {
            foreach (var remote in new[]
            {
                "https://dev.azure.com/foreign/offline-project/_git/offline-repo",
                "https://dev.azure.com/offline-org/foreign/_git/offline-repo",
                "https://dev.azure.com/offline-org/offline-project/_git/offline-repo-other",
                "https://dev.azure.com/offline-org/offline-project/_git/offline-repo.git",
                "https://dev.azure.com.evil.invalid/offline-org/offline-project/_git/offline-repo",
                "https://dev.azure.com/offline-org/offline-project/_git/offline-repo?extra=1",
                "git@ssh.dev.azure.com:v3/foreign/offline-project/offline-repo",
            })
            {
                var fake = new FakeTransport { Remote = remote };
                await using var service = new CopilotRunService(fake);
                var state = await service.StartAsync(Request());
                var done = await Wait(service, state.RunId, s => !s.IsActive);
                Check(done.Status == "failed" && done.Message.Contains("matching") && !fake.Calls.Any(c => c.Operation == "prepare"), "Foreign identity started a worktree");
                Check(!done.Message.Contains(remote), "Remote credentials could leak in diagnostic");
            }
            foreach (var remote in new[]
            {
                "https://dev.azure.com/offline-org/offline-project/_git/offline-repo",
                "https://offline-org.visualstudio.com/offline-project/_git/offline-repo",
                "git@ssh.dev.azure.com:v3/offline-org/offline-project/offline-repo",
                "ssh://git@ssh.dev.azure.com/v3/offline-org/offline-project/offline-repo",
            })
                Check(CopilotRunService.RemoteMatches(remote, "offline-org", "offline-project", "offline-repo"), "Valid ADO remote rejected");
        });

        await test("Copilot push authority requires every effective push URL to be the PR repository before any worktree", async () =>
        {
            const string own = "https://dev.azure.com/offline-org/offline-project/_git/offline-repo";
            const string foreign = "https://user:secret@dev.azure.com/foreign-org/foreign-project/_git/foreign-repo";
            foreach (var pushUrls in new[] { foreign, own + "\n" + foreign, "" })
            {
                var fake = new FakeTransport { PushUrls = pushUrls };
                await using var service = new CopilotRunService(fake);
                var state = await service.StartAsync(Request() with { PushToSource = true });
                var done = await Wait(service, state.RunId, s => !s.IsActive);
                Check(done.Status == "failed" && done.Message.Contains("every effective push URL") && done.Message.Contains("Nothing was pushed") &&
                    !fake.Calls.Any(c => c.Operation is "prepare" or "stage-prompt" or "launch"), "Foreign/unverifiable push destination started a push run");
                Check(!done.Message.Contains("foreign") && !done.Message.Contains("secret"), "Push destination leaked in diagnostic");
            }
            var legacy = new FakeTransport { PushUrls = foreign };
            await using (var service = new CopilotRunService(legacy))
            {
                var run = await service.StartAsync(Request() with { PushToSource = false });
                await legacy.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(legacy.Calls.Single(c => c.Operation == "prepare").Arguments[10] == "", "No-push run was blocked or bound to push destinations");
                legacy.Finish(0);
                await Wait(service, run.RunId, s => !s.IsActive);
            }
        });

        foreach (var (name, help) in new[]
        {
            ("option names only", Help),
            ("current CLI wording", """
                -r, --resume [<value>]
                    Resume a session
                    --session-id <id>
                    --stream <mode>
                    --deny-tool [<tools>...]
                    --yolo
                    --no-ask-user
                        Disable the ask_user tool (agent works autonomously without
                        asking questions)
                    --no-color
                        Disable all color output
                """),
        })
            await test("Copilot advertised CLI options tolerate changed help wording: " + name, async () =>
            {
                var fake = new FakeTransport { CliHelp = help };
                await using var service = new CopilotRunService(fake);
                var run = await service.StartAsync(Request());
                await Wait(service, run.RunId, s => s.Status == "running");
                Check(fake.Calls.Any(c => c.Operation == "launch"), "Advertised options were rejected because of help prose");
                fake.Finish(0);
                await Wait(service, run.RunId, s => !s.IsActive);
            });

        foreach (var option in new[] { "--no-ask-user", "--stream", "--no-color", "--deny-tool", "--yolo", "--session-id", "--resume" })
            foreach (var replacement in new[] { "", option + "-other", "--other-" + option[2..] })
                await test($"Copilot missing CLI option {option} is named and blocks launch (replacement='{replacement}')", async () =>
                {
                    var fake = new FakeTransport { CliHelp = Help.Replace(option, replacement) };
                    await using var service = new CopilotRunService(fake);
                    var run = await service.StartAsync(Request());
                    var done = await Wait(service, run.RunId, s => !s.IsActive);
                    Check(done.Status == "failed" && done.Message.Contains("options: " + option + "."),
                        "Missing option was not identified precisely");
                    Check(done.Message.Contains("configured WSL distribution"), "Diagnostic does not identify which CLI to inspect");
                    Check(!fake.Calls.Any(c => c.Operation is "prepare" or "stage-prompt" or "launch"),
                        "Missing option created a worktree or launched a task");
                });

        await test("Copilot missing mapping/source/tool contract errors never launch a real task", async () =>
        {
            var fake = new FakeTransport();
            await using var service = new CopilotRunService(fake);
            await ExpectError(async () => await service.StartAsync(Request() with { SourceCommit = "" }), "source");
            await ExpectError(async () => await service.StartAsync(Request() with { Directory = "relative/repo" }), "absolute");
            await ExpectError(async () => await service.StartAsync(Request() with { Repo = "../../escape", Directory = null }), "explicit WSL path");
            Check(fake.Calls.IsEmpty, "Invalid configuration invoked WSL");
            fake.FailOperation = "preflight";
            var failed = await service.StartAsync(Request() with { Directory = null, Distro = null });
            var done = await Wait(service, failed.RunId, s => !s.IsActive);
            Check(done.Status == "failed" && done.Message.Contains("preflight") && fake.Calls.Single().Arguments[0] == "", "Unverified default path was used");
            fake.FailOperation = null;
            fake.CliHelp = "--prompt only";
            var legacy = await service.StartAsync(Request());
            Check((await Wait(service, legacy.RunId, s => !s.IsActive)).Message.Contains("headless"), "Old CLI assumed safe headless semantics");
            fake.CliHelp = Help.Replace("--yolo", "");
            var noPermissions = await service.StartAsync(Request());
            Check((await Wait(service, noPermissions.RunId, s => !s.IsActive)).Message.Contains("--yolo"), "CLI without requested permission flag was allowed");
            Check(!fake.Calls.Any(c => c.Operation == "launch"), "Invalid setup launched a task");
        });

        await test("Copilot output snapshots are bounded/throttled and hostile subscribers cannot orphan runs", async () =>
        {
            var fake = new FakeTransport();
            await using var service = new CopilotRunService(fake);
            var notifications = 0;
            service.Changed += _ => throw new Exception("UI subscriber failure");
            service.Changed += _ => Interlocked.Increment(ref notifications);
            var run = await service.StartAsync(Request());
            await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var i = 0; i < 200; i++) fake.Output(new string('x', 2048));
            fake.Output("\nFINAL-OUTPUT\n");
            fake.Finish(0);
            var done = await Wait(service, run.RunId, s => !s.IsActive);
            Check(done.Output.Length <= CopilotRunService.MaxOutputCharacters && done.Output.StartsWith("[Earlier output truncated]") &&
                done.Output.EndsWith("FINAL-OUTPUT\n"), "Output cap lost tail or grew unbounded");
            Check(notifications < 30, "Unthrottled per-chunk snapshots");
            Check(done.Status == "completed" && done.WorktreeStatus == "removed-clean", "Subscriber interrupted cleanup/completion");
        });

        await test("Copilot uncertain stop preserves its reservation; explicit stops are owned and GUI shutdown detaches", async () =>
        {
            var fake = new FakeTransport { FailOperation = "stop" };
            await using (var service = new CopilotRunService(fake))
            {
                var run = await service.StartAsync(Request());
                await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var done = await service.CancelAsync(run.Key, run.RunId);
                Check(done!.Status == "needs-attention" && done.Message.Contains("Could not confirm") && done.WorktreeRetained &&
                    done.MayBeRunning && done.FinishedAt is null, "Uncertain cancellation reported stopped or idle");
                await ExpectError(async () => await service.StartAsync(Request()), "already active");
                await ExpectError(async () => await service.StopAllAsync(), "Keep the app open");
                fake.FailOperation = null;
                await service.StopAllAsync();
                Check(service.Get(run.RunId)!.Status == "cancelled", "Uncertain stop could not be retried before application shutdown");
            }
            fake = new FakeTransport();
            var owned = new CopilotRunService(fake);
            var state = await owned.StartAsync(Request());
            await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await owned.DisposeAsync();
            Check(fake.Calls.All(c => c.Operation != "stop") && owned.Get(state.RunId)!.Status == "running", "GUI shutdown cancelled or completed its supervised task instead of detaching");
        });

        await test("Copilot failure diagnostics never forward transport secrets and failure/cancel worktrees are retained", async () =>
        {
            var fake = new FakeTransport { ThrowOperation = "preflight" };
            await using var service = new CopilotRunService(fake);
            var run = await service.StartAsync(Request());
            var done = await Wait(service, run.RunId, s => !s.IsActive);
            Check(!done.Message.Contains("offline-secret") && done.Status == "failed", "Process exception leaked");
            Check(CopilotRunService.RunnerScript.Contains("unset COPILOT_ALLOW_ALL") &&
                CopilotRunService.RunnerScript.Contains("COPILOT_ASSISTED_APPROVAL=false") &&
                CopilotRunService.RunnerScript.Contains("--yolo") && !CopilotRunService.RunnerScript.Contains("--allow-all") &&
                CopilotRunService.RunnerScript.Contains("denies=(--deny-tool 'shell(git push)' --deny-tool 'shell(gh pr merge)' --deny-tool 'shell(az repos pr update)')") &&
                CopilotRunService.RunnerScript.Contains("if [[ \"$mode\" == *-push ]]; then denies=(--deny-tool 'shell(gh pr merge)' --deny-tool 'shell(az repos pr update)'); fi") &&
                CopilotRunService.RunnerScript.Contains("\"${sessionargs[@]}\" \"${denies[@]}\" <&3"), "Requested permissions or command boundaries changed");
            Check(!CopilotRunService.PrepareScript.Contains(" checkout ") && !CopilotRunService.PrepareScript.Contains(" reset ") &&
                !CopilotRunService.CleanupScript.Contains("--force"), "Main checkout or forced removal mutation");
        });

        await test("Copilot preflight stage and exit reach failed run state without starting preparation", async () =>
        {
            var fake = new FakeTransport { FailOperation = "preflight",
                FailureError = "private URL/token\nADOPRVIEW_PREFLIGHT:copilot:1\n" };
            await using var service = new CopilotRunService(fake);
            var run = await service.StartAsync(Request());
            var done = await Wait(service, run.RunId, s => !s.IsActive);
            Check(done.Message.Contains("~/.local/bin/copilot") && done.Message.Contains("exit 1") &&
                !done.Message.Contains("private") && !done.Output.Contains("private") &&
                fake.Calls.Count == 1, "Preflight failure lost safe diagnostic or continued into preparation");
            Check(fake.Calls.Single().Script.StartsWith(CopilotRunService.PreflightDiagnosticsScript),
                "Stage diagnostics were not installed before environment initialization");
        });

        await test("Copilot failed preparation never claims a created worktree and rejects missing success receipts", async () =>
        {
            foreach (var fail in new[] { true, false })
            {
                var fake = new FakeTransport { FailOperation = fail ? "prepare" : null, MissingReceipt = !fail };
                await using var service = new CopilotRunService(fake);
                var run = await service.StartAsync(Request());
                var done = await Wait(service, run.RunId, s => !s.IsActive);
                Check(done.Status == "failed" && done.WorktreePath is null && done.WorktreeStatus == "not-created" &&
                    !done.WorktreeRetained && !fake.Calls.Any(c => c.Operation is "launch" or "stage-prompt"),
                    "Uncreated/unverified worktree was reported retained or launched");
            }
        });

        await test("Copilot unavailable native WSL command names safe Windows error without raw process details", async () =>
        {
            var fake = new FakeTransport { ThrowWin32Operation = "preflight" };
            await using var service = new CopilotRunService(fake);
            var run = await service.StartAsync(Request());
            var done = await Wait(service, run.RunId, s => !s.IsActive);
            Check(done.Message.Contains("Windows error 2") && done.Message.Contains("No Copilot task was launched") &&
                !done.Message.Contains("offline-secret") && !done.MayBeRunning, "Native WSL failure was hidden or claimed process activity");
        });

        foreach (var transportFailure in new[] { false, true })
        await test($"Copilot lost output following keeps actual owned task active until its real exit (transport={transportFailure})", async () =>
        {
            var fake = new FakeTransport { FailOperation = transportFailure ? null : "follow", ThrowIoOperation = transportFailure ? "follow" : null };
            await using var service = new CopilotRunService(fake);
            var run = await service.StartAsync(Request());
            var monitoring = await Wait(service, run.RunId, s => s.Status == "running" && s.Message.Contains("output", StringComparison.OrdinalIgnoreCase));
            Check(monitoring.IsActive && monitoring.FinishedAt is null &&
                fake.Calls.Any(c => c.Operation == "status"), "Output failure became false terminal state");
            await ExpectError(async () => await service.StartAsync(Request()), "already active");
            fake.Finish(7);
            var done = await Wait(service, run.RunId, s => !s.IsActive);
            Check(done.Status == "failed" && done.ExitCode == 7 && done.WorktreeRetained &&
                !fake.Calls.Any(c => c.Operation == "stop"), "Lost follow killed the task or hid its actual exit");
        });

        if (Environment.GetEnvironmentVariable("ADOPRVIEW_TEST_WSL") == "1")
            await test("Copilot disposable WSL repos verify final --yolo argv, generated review/description prompt/CWD, isolation and dirty/unpushed retention with fake CLI", RunWslWorktrees);
        if (Environment.GetEnvironmentVariable("ADOPRVIEW_TEST_WSL_CONTROL") == "1")
            await test("Copilot kernel owned-group escalation preserves work and logs and refuses orphan lock and foreign receipt", CopilotControlProbe.RunAsync);
        if (Environment.GetEnvironmentVariable("ADOPRVIEW_TEST_NATIVE_ACTION") == "1")
            await test("Native Copilot WPF stop remains responsive with unacknowledged WebView output and hung follower", NativeControl);
    }

    static Task NativeControl()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var profile = Path.Combine(Path.GetTempPath(), "ado-control-browser-" + Guid.NewGuid().ToString("N"));
            var browser = new WebView2();
            var stop = new Button { Content = "Stop exact synthetic owned run" };
            var panel = new DockPanel();
            DockPanel.SetDock(stop, Dock.Top);
            panel.Children.Add(stop); panel.Children.Add(browser);
            var window = new Window { Width = 900, Height = 500, Content = panel, ShowInTaskbar = false };
            window.Loaded += async (_, _) =>
            {
                Exception? failure = null;
                CoreWebView2Environment? environment = null;
                var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var fixture = new CopilotRecoveryTests.Fixture();
                var fake = fixture.Transport;
                fake.BlockFollowers = true;
                var service = fixture.Service();
                Task? recovery = null;
                Task? shutdown = null;
                var releaseShutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                CopilotRunDelivery? delivery = null;
                try
                {
                    environment = await CoreWebView2Environment.CreateAsync(null, profile).WaitAsync(TimeSpan.FromSeconds(15));
                    environment.BrowserProcessExited += (_, _) => exited.TrySetResult();
                    await browser.EnsureCoreWebView2Async(environment).WaitAsync(TimeSpan.FromSeconds(15));
                    var navigated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    browser.CoreWebView2.NavigationCompleted += (_, _) => navigated.TrySetResult();
                    browser.NavigateToString("<!doctype html><title>Bounded synthetic task control</title><p>No live model or ADO.</p><script>window.chrome.webview.addEventListener('message', e => { window.nativeLast = e.data; });</script>");
                    await navigated.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    var posts = 0;
                    long postedRevision = -1;
                    delivery = MainWindow.CreateCopilotDelivery(async (state, id) => await window.Dispatcher.InvokeAsync(() =>
                    {
                        posts++;
                        postedRevision = state.Revision;
                        browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(
                            new { @event = "copilotRun", data = MainWindow.RunForUi(state), deliveryId = id }, Bridge.Json));
                    }), ex => failure ??= ex, new HashSet<string>(StringComparer.Ordinal));
                    service.Changed += delivery.Offer;
                    var run = await service.StartAsync(Request());
                    await Wait(service, run.RunId, state => state.Output.Contains("FAKE-READY"));
                    var saved = fixture.Store.Load().Single().Saved!;
                    var otherId = Guid.NewGuid().ToString("N");
                    fixture.Store.Write(saved with
                    {
                        State = saved.State with
                        {
                            RunId = otherId, Request = Request(99) with { Prompt = "" },
                            SessionId = Guid.NewGuid().ToString(),
                            WorktreePath = saved.State.WorktreePath!.Replace(run.RunId, otherId)
                        },
                        RunDirectory = saved.RunDirectory!.Replace(run.RunId, otherId),
                        OwnershipToken = new string('b', 64), TaskIdentity = null
                    });
                    fake.BlockPreflight = true;
                    recovery = service.RecoverAsync();
                    await fake.RecoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    for (var i = 0; i < 64; i++) fake.Emit("bounded synthetic output\n");
                    fake.Emit("NATIVE-TAIL\n");
                    await Task.Delay(300);
                    Check(posts == 1, "Unacknowledged browser queued more than one output message");
                    shutdown = MainWindow.DisposeCopilotAfterAsync(service, () => releaseShutdown.Task);
                    var stopped = new TaskCompletionSource<CopilotRunState?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    stop.Click += async (_, _) =>
                    {
                        try { stopped.TrySetResult(await service.CancelAsync(run.Key, run.RunId)); }
                        catch (Exception ex) { stopped.TrySetException(ex); }
                    };
                    var timing = Stopwatch.StartNew();
                    stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var state = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    timing.Stop();
                    Check(window.IsVisible && state is { Status: "cancelled", MayBeRunning: false, WorktreeRetained: true } &&
                        state.Output.EndsWith("NATIVE-TAIL\n") && fake.Stops == 1,
                        "Native stop waited for the browser or lost output/work");
                    Check(!recovery.IsCompleted && !fake.ReleaseFollowers.Task.IsCompleted && !shutdown.IsCompleted && posts == 1,
                        "Native stop required blocked recovery/follow/output sentinels to resolve");
                    Check(fixture.Store.Load().Single(record => record.Saved?.State.RunId == run.RunId).Saved!.TaskConfirmedStopped,
                        "Native stop did not preserve its durable exact-task stop proof");
                    Console.WriteLine($"NATIVE_CONTROL stopMs={timing.Elapsed.TotalMilliseconds:F1} deadlineMs=3000 recoveryBlocked=true followerBlocked=true teardownBlocked=true unacknowledgedPosts={posts}");
                    Check((await browser.CoreWebView2.ExecuteScriptAsync("document.title").WaitAsync(TimeSpan.FromSeconds(3))).Contains("Bounded synthetic task control"),
                        "Synthetic dashboard is not responsive");
                    var browserRevision = await browser.CoreWebView2.ExecuteScriptAsync("window.nativeLast?.data?.revision").WaitAsync(TimeSpan.FromSeconds(3));
                    Check(long.TryParse(browserRevision, out var actualRevision) && actualRevision == postedRevision && actualRevision >= 0,
                        "Actual WebView message omitted the projected native lifecycle revision");
                    await delivery.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
                    releaseShutdown.TrySetResult();
                    await shutdown.WaitAsync(TimeSpan.FromSeconds(2));
                    fake.Emit("LATE");
                    Check(service.Get(run.RunId)!.Output == state!.Output, "Hung follower delivered late output after disposal");
                }
                catch (Exception ex) { failure = ex; }
                finally
                {
                    releaseShutdown.TrySetResult();
                    fake.ReleasePreflight.TrySetResult();
                    fake.ReleaseFollowers.TrySetResult();
                    if (recovery is not null)
                        try { await recovery.WaitAsync(TimeSpan.FromSeconds(3)); }
                        catch (Exception ex) { failure ??= ex; }
                    if (shutdown is not null)
                        try { await shutdown.WaitAsync(TimeSpan.FromSeconds(3)); }
                        catch (Exception ex) { failure ??= ex; }
                    try
                    {
                        if (delivery is not null) await delivery.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                    }
                    catch (Exception ex) { failure ??= ex; }
                    fake.End(143);
                    browser.Dispose(); window.Close();
                    try
                    {
                        if (environment is not null) await exited.Task.WaitAsync(TimeSpan.FromSeconds(15));
                        if (Directory.Exists(profile)) Directory.Delete(profile, recursive: true);
                    }
                    catch (Exception ex) { failure ??= ex; }
                    if (failure is null) done.TrySetResult(); else done.TrySetException(failure);
                    Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            };
            window.Show();
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }

    sealed class FakeTransport : ICopilotRunTransport, IDisposable
    {
        public readonly ConcurrentQueue<CopilotProcessRequest> Calls = new();
        public string Remote = "https://dev.azure.com/offline-org/offline-project/_git/offline-repo";
        public string? PushUrls;
        public const string PushHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        public string CliHelp = Help;
        public string? FailOperation, ThrowOperation, ThrowIoOperation, ThrowWin32Operation;
        public string? HungOperation;
        public bool ThrowCancellation;
        public bool BlockCleanup;
        public readonly TaskCompletionSource CleanupEntered = NewSignal();
        public readonly TaskCompletionSource CleanupReleased = NewSignal();
        public readonly TaskCompletionSource ControlEntered = NewSignal();
        public readonly TaskCompletionSource ControlReleased = NewSignal();
        public bool MissingReceipt, IgnoreFollowCancellation, BlockCancellation;
        public readonly ManualResetEventSlim ReleaseCancellation = new();
        public readonly TaskCompletionSource CancellationFinished = NewSignal();
        public readonly TaskCompletionSource CancellationEntered = NewSignal();
        public string FailureError = "offline-secret";
        public TaskCompletionSource Started = NewSignal();
        TaskCompletionSource<int> completion = NewCompletion();
        Action<string>? stdout, stderr;
        public void Output(string text) => stdout!(text);
        public void Error(string text) => stderr!(text);
        public void Finish(int code) => completion.TrySetResult(code);
        public void Reset() { Started = NewSignal(); completion = NewCompletion(); }
        public void Dispose() => ReleaseCancellation.Dispose();
        static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        static TaskCompletionSource<int> NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<CopilotProcessResult> ExecuteAsync(CopilotProcessRequest request, Action<string>? output,
            Action<string>? error, CancellationToken token)
        {
            Calls.Enqueue(request);
            token.ThrowIfCancellationRequested();
            if (HungOperation == request.Operation)
            {
                stdout = output; stderr = error;
                using var registration = token.Register(() =>
                {
                    try
                    {
                        CancellationEntered.TrySetResult();
                        ReleaseCancellation.Wait(TimeSpan.FromSeconds(5));
                        if (ThrowCancellation) throw new InvalidOperationException("synthetic cancellation failure");
                    }
                    finally { CancellationFinished.TrySetResult(); }
                });
                ControlEntered.TrySetResult();
                await ControlReleased.Task;
                return new(0, "", "");
            }
            if (ThrowOperation == request.Operation) throw new InvalidOperationException("offline-secret");
            if (ThrowIoOperation == request.Operation) throw new IOException("offline-secret");
            if (ThrowWin32Operation == request.Operation) throw new System.ComponentModel.Win32Exception(2, "offline-secret");
            if (FailOperation == request.Operation) return new(1, "", FailureError);
            string Protocol(params string[] values) => "ADOPRVIEW1\0" + string.Join('\0', values) + "\0";
            switch (request.Operation)
            {
                case "preflight":
                    return new(0, Protocol("Offline-Ubuntu", "offline", "/home/offline", "/home/offline/source",
                        "/home/offline/source/.git", "/home/offline/bin/copilot", "/home/offline/bin/bgtask", CliHelp, "/home/offline/.copilot", "origin", Remote, PushUrls ?? Remote, PushHash), "");
                case "prepare":
                    if (MissingReceipt) return new(0, "", "");
                    var run = "/home/offline/.local/state/ado-prview/runs/" + request.Arguments[6];
                    return new(0, Protocol("WORKTREE", run, run + "/worktree"), "");
                case "follow":
                    var following = IgnoreFollowCancellation ? completion.Task : completion.Task.WaitAsync(token);
                    using (token.Register(() =>
                    {
                        try
                        {
                            CancellationEntered.TrySetResult();
                            if (BlockCancellation) ReleaseCancellation.Wait(TimeSpan.FromSeconds(5));
                        }
                        finally { CancellationFinished.TrySetResult(); }
                    }))
                    {
                        stdout = output; stderr = error; Started.TrySetResult();
                        await following;
                        return new(0, "", "");
                    }
                case "status":
                    return new(0, System.Text.Json.JsonSerializer.Serialize(new
                    {
                        status = new { state = completion.Task.IsCompletedSuccessfully || token.IsCancellationRequested || Calls.Any(c => c.Operation == "stop") ? "exited" : "running",
                            exited = new { code = completion.Task.IsCompletedSuccessfully ? completion.Task.Result : 143 } }
                    }), "");
                case "cleanup":
                    if (BlockCleanup)
                    {
                        CleanupEntered.TrySetResult();
                        await CleanupReleased.Task.WaitAsync(token);
                    }
                    return new(0, Protocol("REMOVED"), "");
                default: return new(0, "", "");
            }
        }
    }

    static async Task RunWslWorktrees()
    {
        await using var fixture = await WslFixture.Create();
        await using var service = new CopilotRunService(fixture);
        var reviewPrompt = await CopilotSourceTests.CreatePrompt();
        var a = await service.StartAsync(fixture.Request(1, "review-gate") with { Prompt = reviewPrompt, PushToSource = true });
        var b = await service.StartAsync(fixture.Request(2, "clean-b") with { SourceCommit = fixture.SecondCommit, SourceRef = "refs/heads/second" });
        await Wait(service, a.RunId, s => s.Output.Contains("FAKE-READY"));
        await Wait(service, b.RunId, s => s.Output.Contains("FAKE-READY"));
        var first = service.Get(a.RunId)!; var second = service.Get(b.RunId)!;
        Check(first.WorktreePath != second.WorktreePath && first.Output.Contains(fixture.Commit) &&
            second.Output.Contains(fixture.SecondCommit), "Concurrent worktrees used the wrong source");
        foreach (var (run, mode, prompt) in new[] { (first, "review-gate", reviewPrompt), (second, "clean-b", "clean-b") })
        {
            using var launch = await fixture.ReadLaunch(mode);
            var final = launch.RootElement;
            Check(final.GetProperty("args").EnumerateArray().Select(v => v.GetString()).SequenceEqual(FinalArgs(run, resumed: false)),
                "Requested permission arguments did not reach the final CLI through bgtask");
            Check(final.GetProperty("cwd").GetString() == run.WorktreePath &&
                final.GetProperty("prompt").GetString() == prompt + CopilotRunService.PublishInstructions(run.Request, "origin", fixture.PushHash),
                "Final CLI received the wrong isolated CWD or stdin prompt");
            if (mode == "review-gate") CopilotSourceTests.CheckReviewGate(final.GetProperty("prompt").GetString()!);
            Check(final.GetProperty("allowAllEnvironment").ValueKind == JsonValueKind.Null &&
                final.GetProperty("assistedApproval").GetString() == "false", "Inherited permissions overrode the deliberate launch");
        }
        await fixture.Release("review-gate", "clean-b");
        first = await Wait(service, a.RunId, s => !s.IsActive && s.WorktreeStatus is "removed-clean" or "retained-changes" or "retained");
        second = await Wait(service, b.RunId, s => !s.IsActive && s.WorktreeStatus is "removed-clean" or "retained-changes" or "retained");
        Check(first.Status == "completed" && second.Status == "completed" &&
            first.WorktreeStatus == "removed-clean" && second.WorktreeStatus == "removed-clean",
            "Clean ephemeral worktrees retained: " + first.Message + "; " + second.Message + "; " + fixture.Diagnostic);
        var modes = new[] { "dirty", "ignored", "commit", "fail", "cancel" };
        foreach (var mode in modes)
        {
            var run = await service.StartAsync(fixture.Request(10 + Array.IndexOf(modes, mode), mode));
            await Wait(service, run.RunId, s => s.Output.Contains("FAKE-READY"));
            if (mode == "cancel") await service.CancelAsync(run.Key, run.RunId);
            else await fixture.Release(mode);
            var done = await Wait(service, run.RunId, s => !s.IsActive);
            Check(done.WorktreeRetained, mode + " worktree was deleted");
            Check(await fixture.Exists(done.WorktreePath!), mode + " retained path does not exist");
        }
        var stale = await service.StartAsync(fixture.Request(30, "must-never-launch") with { SourceCommit = fixture.SecondCommit });
        var rejected = await Wait(service, stale.RunId, s => !s.IsActive);
        Check(rejected.Status == "failed" && !rejected.Output.Contains("FAKE-READY") && rejected.Message.Contains("source ref"),
            "Source ref/hash mismatch launched a stale or unrelated commit");

        var firstApp = new CopilotRunService(fixture, fixture.HistoryDirectory);
        Console.WriteLine("WSL fixture: checking durable detach/reattach");
        // One push-authorized and one no-push run must each keep their own authority across restart and exact-session resume.
        var resumables = new[]
        {
            await firstApp.StartAsync(fixture.Request(40, "resume-test") with { PushToSource = true }),
            await firstApp.StartAsync(fixture.Request(42, "resume-legacy")),
        };
        foreach (var r in resumables) await Wait(firstApp, r.RunId, s => s.Output.Contains("FAKE-READY"));
        await firstApp.DisposeAsync();
        Console.WriteLine("WSL fixture: detached first app");
        var secondApp = new CopilotRunService(fixture, fixture.HistoryDirectory);
        await secondApp.RecoverAsync();
        foreach (var r in resumables) await Wait(secondApp, r.RunId, s => s.IsActive && s.Output.Contains("FAKE-READY"));
        foreach (var mode in new[] { "resume-test", "resume-legacy" })
            using (var launch = await fixture.ReadLaunch(mode))
                Check(!launch.RootElement.GetProperty("resumed").GetBoolean(), "Restart reattached by launching a second CLI");
        await secondApp.DisposeAsync();
        Console.WriteLine("WSL fixture: detached second app; interrupting only the fake tasks");
        foreach (var r in resumables) await fixture.Interrupt(r.RunId);
        await using var thirdApp = new CopilotRunService(fixture, fixture.HistoryDirectory);
        await thirdApp.RecoverAsync();
        Console.WriteLine("WSL fixture: reconciled exact-session resume");
        foreach (var (resumable, mode) in new[] { (resumables[0], "resume-test"), (resumables[1], "resume-legacy") })
        {
            var continued = await Wait(thirdApp, resumable.RunId, s => s.Output.Contains("FAKE-RESUMED"));
            Check(continued.Request.PushToSource == (mode == "resume-test"), "Restart changed the persisted push authority");
            using var launch = await fixture.ReadLaunch(mode);
            var final = launch.RootElement;
            Check(final.GetProperty("resumed").GetBoolean() && final.GetProperty("session").GetString() == resumable.SessionId &&
                final.GetProperty("prompt").GetString() == "" && final.GetProperty("cwd").GetString() == continued.WorktreePath &&
                continued.ResumeAttempts == 1, "Real WSL continuation lost session/CWD or replayed an original/new prompt");
            Check(final.GetProperty("args").EnumerateArray().Select(v => v.GetString()).SequenceEqual(FinalArgs(continued, resumed: true)),
                "Resumed final CLI argv did not keep the run's original push authority");
        }
        await fixture.Release("resume-test", "resume-legacy");
        foreach (var r in resumables) await Wait(thirdApp, r.RunId, s => s.Status == "completed" && s.WorktreeStatus == "removed-clean");
        await thirdApp.DisposeAsync();
        await using var fourthApp = new CopilotRunService(fixture, fixture.HistoryDirectory);
        await fourthApp.RecoverAsync();
        Check(resumables.All(r => fourthApp.Get(r.RunId)!.Status == "completed"), "Cleaned success resumed on restart");

        var published = await service.StartAsync(fixture.Request(41, "published"));
        await Wait(service, published.RunId, s => s.Output.Contains("FAKE-READY"));
        await fixture.Release("published");
        var publishedDone = await Wait(service, published.RunId, s => !s.IsActive && s.WorktreeStatus is "removed-clean" or "retained-changes" or "retained");
        Check(publishedDone.Status == "completed" && publishedDone.WorktreeStatus == "removed-clean",
            "Safely published changed HEAD was unnecessarily retained or unverified");

        Console.WriteLine("WSL fixture: prompt-directed source-branch publication");
        var foreignBefore = await fixture.RemoteRefs("foreign.git");
        var before = await fixture.RemoteRefs();
        var pushed = await service.StartAsync(fixture.Request(43, "push-source") with { SourceRef = WslFixture.PushRef, PushToSource = true });
        await Wait(service, pushed.RunId, s => s.Output.Contains("FAKE-READY"));
        await fixture.Release("push-source");
        var pushedDone = await Wait(service, pushed.RunId, s => !s.IsActive && s.WorktreeStatus is "removed-clean" or "retained-changes" or "retained");
        var after = await fixture.RemoteRefs();
        Check(pushedDone.Status == "completed" && pushedDone.Output.Contains("FAKE-PUSH rc=0") && pushedDone.Output.Contains("FAKE-VERIFIED") &&
            pushedDone.WorktreeStatus == "removed-clean", "Explicit source-branch push was not published/verified: " + pushedDone.Output + fixture.Diagnostic);
        Check(after[WslFixture.PushRef] != before[WslFixture.PushRef] &&
            after.Where(p => p.Key != WslFixture.PushRef).OrderBy(p => p.Key).SequenceEqual(before.Where(p => p.Key != WslFixture.PushRef).OrderBy(p => p.Key)),
            "Publication changed refs other than the exact captured source branch");
        Check(pushedDone.Output.Contains("FAKE-TAG") && !after.Keys.Any(k => k.StartsWith("refs/tags/")) &&
            (await fixture.RemoteRefs("foreign.git")).OrderBy(p => p.Key).SequenceEqual(foreignBefore.OrderBy(p => p.Key)),
            "push.followTags published a tag or a foreign destination changed");

        Console.WriteLine("WSL fixture: unsafe push destinations block before publication");
        var cases = new (string Mode, string[][] Set, string[][] Unset)[]
        {
            ("blocked-pushurl", [["remote.origin.pushurl", WslFixture.ForeignUrl]], [["--unset-all", "remote.origin.pushurl"]]),
            ("blocked-multi", [["--add", "remote.origin.pushurl", WslFixture.OwnUrl], ["--add", "remote.origin.pushurl", WslFixture.ForeignUrl]],
                [["--unset-all", "remote.origin.pushurl"]]),
            ("blocked-insteadof", [["url." + WslFixture.ForeignUrl + ".pushInsteadOf", WslFixture.OwnUrl]],
                [["--unset-all", "url." + WslFixture.ForeignUrl + ".pushInsteadOf"]]),
        };
        foreach (var (mode, set, unset) in cases)
        {
            foreach (var args in set) await fixture.GitConfig(args);
            var refsBefore = await fixture.RemoteRefs();
            var blocked = await service.StartAsync(fixture.Request(50 + Array.FindIndex(cases, c => c.Mode == mode), mode) with { PushToSource = true });
            var blockedDone = await Wait(service, blocked.RunId, s => !s.IsActive);
            Check(blockedDone.Status == "failed" && blockedDone.WorktreePath == null && !blockedDone.Output.Contains("FAKE-READY") &&
                blockedDone.Message.Contains("every effective push URL") && !blockedDone.Message.Contains("foreign"),
                mode + " did not block before any worktree/launch: " + blockedDone.Message);
            Check((await fixture.RemoteRefs()).OrderBy(p => p.Key).SequenceEqual(refsBefore.OrderBy(p => p.Key)) &&
                (await fixture.RemoteRefs("foreign.git")).OrderBy(p => p.Key).SequenceEqual(foreignBefore.OrderBy(p => p.Key)), mode + " changed a remote");
            if (mode == "blocked-pushurl")
            {
                // Publication-only configuration does not block a run that is not authorized to push.
                var legacy = await service.StartAsync(fixture.Request(60, "legacy-foreign") with { SourceCommit = fixture.SecondCommit, SourceRef = "refs/heads/second" });
                await Wait(service, legacy.RunId, s => s.Output.Contains("FAKE-READY"));
                await fixture.Release("legacy-foreign");
                Check((await Wait(service, legacy.RunId, s => !s.IsActive && s.WorktreeStatus is "removed-clean" or "retained-changes" or "retained")).Status == "completed",
                    "No-push run was blocked by push-only configuration");
            }
            foreach (var args in unset) await fixture.GitConfig(args);
        }

        Console.WriteLine("WSL fixture: destination changed after launch is caught by the generated push command");
        var late = await service.StartAsync(fixture.Request(61, "push-late") with { SourceRef = "refs/heads/users/late", PushToSource = true });
        await Wait(service, late.RunId, s => s.Output.Contains("FAKE-READY"));
        await fixture.GitConfig("--add", "remote.origin.pushurl", WslFixture.ForeignUrl);
        before = await fixture.RemoteRefs();
        await fixture.Release("push-late");
        var lateDone = await Wait(service, late.RunId, s => !s.IsActive);
        await fixture.GitConfig("--unset-all", "remote.origin.pushurl");
        Check(lateDone.Output.Contains("FAKE-PUSH rc=1") && !lateDone.Output.Contains("FAKE-VERIFIED") && lateDone.WorktreeRetained &&
            await fixture.Exists(lateDone.WorktreePath!), "Changed push destination was not refused before publication");
        Check((await fixture.RemoteRefs()).OrderBy(p => p.Key).SequenceEqual(before.OrderBy(p => p.Key)) &&
            (await fixture.RemoteRefs("foreign.git")).OrderBy(p => p.Key).SequenceEqual(foreignBefore.OrderBy(p => p.Key)), "Late destination change reached a remote");

        Console.WriteLine("WSL fixture: inherited push.recurseSubmodules=on-demand cannot publish a submodule repository");
        var subBefore = await fixture.RemoteRefs("sub.git");
        before = await fixture.RemoteRefs();
        var subUnpushed = await service.StartAsync(fixture.Request(62, "push-sub-unpushed") with
            { SourceRef = "refs/heads/users/sub-unpushed", SourceCommit = fixture.SubCommit, PushToSource = true });
        await Wait(service, subUnpushed.RunId, s => s.Output.Contains("FAKE-READY"));
        await fixture.Release("push-sub-unpushed");
        var subUnpushedDone = await Wait(service, subUnpushed.RunId, s => !s.IsActive);
        Check(subUnpushedDone.Output.Contains("FAKE-CONTROL-RECURSED"),
            "Control did not show inherited on-demand recursion publishing the submodule; the guard proof would be vacuous: " + subUnpushedDone.Output);
        Check(subUnpushedDone.Output.Contains("FAKE-PUSH rc=1") && subUnpushedDone.Output.Contains("FAKE-SUBMODULE-CHECK") &&
            !subUnpushedDone.Output.Contains("FAKE-VERIFIED") && subUnpushedDone.WorktreeRetained && await fixture.Exists(subUnpushedDone.WorktreePath!),
            "Unpublished submodule commit was not refused before publication with work retained: " + subUnpushedDone.Output);
        Check((await fixture.RemoteRefs()).OrderBy(p => p.Key).SequenceEqual(before.OrderBy(p => p.Key)) &&
            (await fixture.RemoteRefs("sub.git")).OrderBy(p => p.Key).SequenceEqual(subBefore.OrderBy(p => p.Key)),
            "Refused submodule push changed the parent or submodule remote");
        var subPublished = await service.StartAsync(fixture.Request(63, "push-sub-published") with
            { SourceRef = "refs/heads/users/sub-published", SourceCommit = fixture.SubCommit, PushToSource = true });
        await Wait(service, subPublished.RunId, s => s.Output.Contains("FAKE-READY"));
        await fixture.Release("push-sub-published");
        var subPublishedDone = await Wait(service, subPublished.RunId, s => !s.IsActive && s.WorktreeStatus is "removed-clean" or "retained-changes" or "retained");
        after = await fixture.RemoteRefs();
        const string subRef = "refs/heads/users/sub-published";
        Check(subPublishedDone.Output.Contains("FAKE-PUSH rc=0") && subPublishedDone.Output.Contains("FAKE-VERIFIED") &&
            !subPublishedDone.Output.Contains("FAKE-SUBMODULE-CHECK"), "Already-published submodule commit blocked the parent push: " + subPublishedDone.Output);
        Check(after[subRef] != before[subRef] && !after.Keys.Any(k => k.StartsWith("refs/tags/")) &&
            after.Where(p => p.Key != subRef).OrderBy(p => p.Key).SequenceEqual(before.Where(p => p.Key != subRef).OrderBy(p => p.Key)) &&
            (await fixture.RemoteRefs("sub.git")).OrderBy(p => p.Key).SequenceEqual(subBefore.OrderBy(p => p.Key)),
            "Parent push with a published submodule commit changed more than the captured source branch");
        // Git refuses to remove a worktree with an initialized submodule; cleanup must report that truthfully and keep everything owned.
        Check(subPublishedDone.Status == "completed" && subPublishedDone.WorktreeStatus == "retained-changes" &&
            subPublishedDone.Message.Contains("git refused to remove the worktree") && await fixture.Exists(subPublishedDone.WorktreePath!) &&
            await fixture.OwnedWorktreeKept(subPublishedDone.RunId, subPublishedDone.WorktreePath!),
            "Refused worktree removal was reported as removed or lost its registration/branch/refs: " + subPublishedDone.Message + fixture.Diagnostic);

        Console.WriteLine("WSL fixture: startup cleanup of a refused worktree removal stays completed and retained");
        var detachedApp = new CopilotRunService(fixture, fixture.HistoryDirectory);
        var subStartup = await detachedApp.StartAsync(fixture.Request(64, "push-sub-startup") with
            { SourceRef = "refs/heads/users/sub-startup", SourceCommit = fixture.SubCommit, PushToSource = true });
        await Wait(detachedApp, subStartup.RunId, s => s.Output.Contains("FAKE-READY"));
        await detachedApp.DisposeAsync();
        before = await fixture.RemoteRefs();
        await fixture.Release("push-sub-startup");
        await fixture.WaitTaskExited(subStartup.RunId);
        foreach (var restart in new[] { 1, 2 })
        {
            await using var startupApp = new CopilotRunService(fixture, fixture.HistoryDirectory);
            await startupApp.RecoverAsync();
            var recovered = await Wait(startupApp, subStartup.RunId, s => !s.IsActive && s.WorktreeStatus is "removed-clean" or "retained-changes" or "retained");
            using var launch = await fixture.ReadLaunch("push-sub-startup");
            Check(recovered.Status == "completed" && recovered.WorktreeStatus == "retained-changes" && recovered.ResumeAttempts == 0 &&
                !launch.RootElement.GetProperty("resumed").GetBoolean() && recovered.Message.Contains("git refused to remove the worktree") &&
                await fixture.Exists(recovered.WorktreePath!) && await fixture.OwnedWorktreeKept(recovered.RunId, recovered.WorktreePath!),
                $"Startup cleanup (restart {restart}) resumed, misreported or removed owned state: " + recovered.Message + fixture.Diagnostic);
        }
        after = await fixture.RemoteRefs();
        Check(after["refs/heads/users/sub-startup"] != before["refs/heads/users/sub-startup"] &&
            after.Where(p => p.Key != "refs/heads/users/sub-startup").OrderBy(p => p.Key).SequenceEqual(
                before.Where(p => p.Key != "refs/heads/users/sub-startup").OrderBy(p => p.Key)),
            "Detached submodule run did not publish exactly its captured source branch");

        var rejected2 = await service.StartAsync(fixture.Request(44, "push-nonff") with { SourceRef = WslFixture.NonFastForwardRef, PushToSource = true });
        await Wait(service, rejected2.RunId, s => s.Output.Contains("FAKE-READY"));
        // The source branch moves concurrently after the run's worktree was created.
        await fixture.MoveRemote(WslFixture.NonFastForwardRef, fixture.SecondCommit);
        before = await fixture.RemoteRefs();
        await fixture.Release("push-nonff");
        var rejectedDone = await Wait(service, rejected2.RunId, s => !s.IsActive);
        Check(rejectedDone.Output.Contains("FAKE-PUSH rc=1") && rejectedDone.WorktreeRetained &&
            await fixture.Exists(rejectedDone.WorktreePath!), "Non-fast-forward rejection was not preserved as unpublished work");
        Check((await fixture.RemoteRefs()).OrderBy(p => p.Key).SequenceEqual(before.OrderBy(p => p.Key)), "Rejected push changed remote refs");
        Check(await fixture.MainUnchanged(), "Main checkout HEAD, index, dirty work files or untracked file changed");
    }

    static string?[] FinalArgs(CopilotRunState run, bool resumed) =>
        new[] { "--stream", "on", "--no-color", "--no-ask-user", "--yolo", resumed ? "--resume" : "--session-id", run.SessionId }
            .Concat(resumed ? new[] { "--prompt", "Continue the interrupted task in this saved session from where it stopped; preserve its original instructions and scope. Do not start a new task." } : [])
            .Concat(run.Request.PushToSource ? [] : new[] { "--deny-tool", "shell(git push)" })
            .Concat(new[] { "--deny-tool", "shell(gh pr merge)", "--deny-tool", "shell(az repos pr update)" }).ToArray();

    sealed class WslFixture : ICopilotRunTransport, IAsyncDisposable
    {
        readonly WslCopilotRunTransport native = new();
        readonly string windowsDirectory;
        public string HistoryDirectory => Path.Combine(windowsDirectory, "run-history");
        string directory = "";
        public string Commit = "", SecondCommit = "", SubCommit = "", PushHash = "";
        readonly List<string> ownedTasks = [];
        public string Diagnostic = "";
        WslFixture(string path) => windowsDirectory = path;

        public CopilotRunRequest Request(int id, string mode) => CopilotRunTests.Request(id, mode) with
        { Title = "offline-" + mode, Directory = directory + "/source", Distro = null, SourceCommit = Commit };

        public static async Task<WslFixture> Create()
        {
            var path = Path.Combine(Environment.CurrentDirectory, ".copilot-run-fixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            var f = new WslFixture(path);
            try
            {
                var translated = await f.native.ExecuteAsync(new("fixture-translate", null, "wslpath -u -- \"$1\"", [path]), null, null, default);
                Check(translated.ExitCode == 0, "WSL fixture path conversion failed");
                f.directory = translated.StandardOutput.Trim();
                var setup = await f.Shell(SetupScript, [f.directory]);
                Check(setup.ExitCode == 0, "Offline WSL fixture setup failed: " + setup.StandardError);
                var commits = setup.StandardOutput.Trim().Split('\n');
                f.Commit = commits[^4].Trim(); f.SecondCommit = commits[^3].Trim(); f.SubCommit = commits[^2].Trim(); f.PushHash = commits[^1].Trim();
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }

        public async Task<CopilotProcessResult> ExecuteAsync(CopilotProcessRequest r, Action<string>? stdout, Action<string>? stderr, CancellationToken token)
        {
            if (r.Operation == "launch") lock (ownedTasks) ownedTasks.Add(r.Arguments[1]);
            // Fixture values are positional parameters, not interpolated into shell source.
            var script = """
                export HOME="$1/home" XDG_CONFIG_HOME="$1/home/.config"
                export PATH="$1/bin:/usr/bin:/bin"
                export COPILOT_ALLOW_ALL=false COPILOT_ASSISTED_APPROVAL=true
                export COPILOT_HOME="$HOME/.copilot"
                unset ADO_PRVIEW_BASH_ENV BASH_ENV
                shift
                """ + "\n" + r.Script;
            var result = await native.ExecuteAsync(r with { Script = script, Arguments = new[] { directory }.Concat(r.Arguments).ToArray() }, stdout, stderr, token);
            if (r.Operation is "cleanup" or "verify-run" && result.ExitCode != 0)
                Diagnostic = r.Operation + ": " + result.ExitCode + " " + result.StandardOutput + " " + result.StandardError;
            return result;
        }

        Task<CopilotProcessResult> Shell(string script, string[] args) =>
            native.ExecuteAsync(new("fixture-command", null, script, args), null, null, default);

        public async Task Release(params string[] modes)
        {
            var r = await Shell("""
                root="$1"; shift
                for mode in "$@"; do
                  python3 -c 'import pathlib,socket,sys; port=int(pathlib.Path(sys.argv[1]).read_text()); s=socket.create_connection(("127.0.0.1",port),timeout=5); s.sendall(b"go"); s.close()' "$root/home/gate-$mode"
                done
                """,
                new[] { directory }.Concat(modes).ToArray());
            Check(r.ExitCode == 0, "Could not release fake CLI");
        }

        public async Task Interrupt(string runId)
        {
            var result = await Shell("""
                export XDG_CONFIG_HOME="$1/home/.config"
                "$1/bin/bgtask" stop "$2" --timeout 1s
                """, [directory, "ado-prview-" + runId]);
            Check(result.ExitCode == 0, "Could not interrupt the exact disposable fake task");
        }

        public async Task<bool> OwnedWorktreeKept(string runId, string worktree) => (await Shell("""
            cd -- "$1/source"
            git worktree list --porcelain | grep -qxF "worktree $3" &&
            git for-each-ref --format='%(refname)' refs/heads/ado-prview/ | grep -q -- "-$2\$" &&
            git rev-parse -q --verify "refs/ado-prview/$2/source" >/dev/null &&
            git rev-parse -q --verify "refs/ado-prview/$2/published" >/dev/null
            """, [directory, runId, worktree])).ExitCode == 0;
        public async Task WaitTaskExited(string runId) => Check((await Shell("""
            export XDG_CONFIG_HOME="$1/home/.config"
            for _ in $(seq 120); do
              "$1/bin/bgtask" status "$2" --json | python3 -c 'import json,sys; sys.exit(json.load(sys.stdin)["status"]["state"]!="exited")' && exit 0
              sleep 1
            done
            exit 1
            """, [directory, "ado-prview-" + runId])).ExitCode == 0, "Disposable fake task did not exit");
        public async Task<bool> Exists(string path) => (await Shell("[[ -d \"$1\" ]]", [path])).ExitCode == 0;
        public async Task<JsonDocument> ReadLaunch(string mode)
        {
            var result = await Shell("cat -- \"$1/home/launch-$2.json\"", [directory, mode]);
            Check(result.ExitCode == 0, "Fake CLI did not record its final launch");
            return JsonDocument.Parse(result.StandardOutput);
        }
        public const string PushRef = "refs/heads/users/it's/push", NonFastForwardRef = "refs/heads/users/nonff";
        public const string ForeignUrl = "https://dev.azure.com/foreign-org/foreign-project/_git/foreign-repo",
            OwnUrl = "https://dev.azure.com/offline-org/offline-project/_git/offline-repo";
        public async Task GitConfig(params string[] args) =>
            Check((await Shell("cd -- \"$1/source\" && shift && git config \"$@\"", new[] { directory }.Concat(args).ToArray())).ExitCode == 0,
                "Could not change disposable source Git configuration");
        public async Task<Dictionary<string, string>> RemoteRefs(string repo = "remote.git")
        {
            var r = await Shell("git --git-dir=\"$1/$2\" for-each-ref --format='%(objectname) %(refname)'", [directory, repo]);
            Check(r.ExitCode == 0, "Could not read disposable remote refs");
            return r.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToDictionary(l => l[41..], l => l[..40]);
        }
        public async Task MoveRemote(string name, string commit) =>
            Check((await Shell("git --git-dir=\"$1/remote.git\" update-ref \"$2\" \"$3\"", [directory, name, commit])).ExitCode == 0,
                "Could not move disposable remote ref");
        public async Task<bool> MainUnchanged()
        {
            var r = await Shell("""
                cd -- "$1/source"
                [[ "$(git rev-parse HEAD)" == "$(cat ../main-head)" ]] &&
                [[ "$(sha256sum .git/index)" == "$(cat ../main-index)" ]] &&
                [[ "$(cat tracked)" == "main dirty content" && "$(cat untracked)" == "main untracked content" ]] &&
                [[ "$(git status --porcelain)" == "$(cat ../main-status)" ]]
                """, [directory]);
            return r.ExitCode == 0;
        }

        public async ValueTask DisposeAsync()
        {
            if (directory.Length != 0)
            {
                foreach (var name in ownedTasks)
                    await Shell("""
                        export XDG_CONFIG_HOME="$1/home/.config"
                        "$HOME/go/bin/bgtask" rm "$2" --force >/dev/null 2>&1 || true
                        """, [directory, name]);
                // Only synthetic fixture data is removed; production never recursively removes worktrees.
                await Shell("rm -rf -- \"$1\"", [directory]);
            }
            if (Directory.Exists(windowsDirectory)) Directory.Delete(windowsDirectory, recursive: true);
        }

        const string SetupScript = """
            set -euo pipefail
            root="$1"; realgit="$(command -v git)"
            bg="$(command -v bgtask || true)"
            [[ -n "$bg" ]] || bg="$HOME/go/bin/bgtask"
            [[ -x "$bg" ]]
            mkdir -p "$root/source" "$root/bin" "$root/home/.config"
            git init -q "$root/source"
            cd "$root/source"
            printf 'initial\n' > tracked
            printf 'ignored-change\n' > .gitignore
            git add tracked .gitignore
            git -c user.name=Offline -c user.email=offline@example.invalid commit -qm initial
            git branch fix-test
            git branch "users/it's/push"
            git branch users/nonff
            git branch users/late
            git checkout -qb second
            printf 'second\n' > tracked
            git add tracked
            git -c user.name=Offline -c user.email=offline@example.invalid commit -qm second
            second="$(git rev-parse HEAD)"
            git checkout -q fix-test
            commit="$(git rev-parse HEAD)"
            # A separate submodule repository: s1 is referenced by the PR branches, s2 is already published on branch "published".
            git init -q "$root/subwork"
            git -C "$root/subwork" -c user.name=Offline -c user.email=offline@example.invalid commit --allow-empty -qm s1
            s1="$(git -C "$root/subwork" rev-parse HEAD)"
            git -C "$root/subwork" -c user.name=Offline -c user.email=offline@example.invalid commit --allow-empty -qm s2
            git -C "$root/subwork" branch published
            git clone -q --bare "$root/subwork" "$root/sub.git"
            rm -rf -- "$root/subwork"
            printf '[submodule "lib"]\n\tpath = lib\n\turl = https://dev.azure.com/offline-org/offline-project/_git/offline-sub\n' > "$root/gitmodules"
            blob="$(git hash-object -w "$root/gitmodules")"
            GIT_INDEX_FILE="$root/sub-index" git read-tree HEAD
            GIT_INDEX_FILE="$root/sub-index" git update-index --add --cacheinfo "160000,$s1,lib" --cacheinfo "100644,$blob,.gitmodules"
            subcommit="$(git -c user.name=Offline -c user.email=offline@example.invalid commit-tree "$(GIT_INDEX_FILE="$root/sub-index" git write-tree)" -p HEAD -m submodule)"
            rm -f -- "$root/sub-index" "$root/gitmodules"
            git branch users/sub-unpushed "$subcommit"
            git branch users/sub-published "$subcommit"
            git branch users/sub-startup "$subcommit"
            git clone -q --bare . "$root/remote.git"
            git clone -q --bare . "$root/foreign.git"
            git remote add origin https://dev.azure.com/offline-org/offline-project/_git/offline-repo
            # push.followTags would publish reachable annotated tags with an explicit refspec; prove the setting is live here.
            git config push.followTags true
            git config push.recurseSubmodules on-demand
            git init -q --bare "$root/control.git"
            git -c user.name=Offline -c user.email=offline@example.invalid tag -a control-tag -m control
            git push -q "$root/control.git" HEAD:refs/heads/control
            git --git-dir="$root/control.git" rev-parse -q --verify refs/tags/control-tag >/dev/null
            git tag -d control-tag >/dev/null
            rm -rf -- "$root/control.git"
            pushhash="$(HOME="$root/home" XDG_CONFIG_HOME="$root/home/.config" git remote get-url --push --all origin | sha256sum)"
            printf 'main dirty content\n' > tracked
            printf 'main untracked content\n' > untracked
            git rev-parse HEAD > ../main-head
            sha256sum .git/index > ../main-index
            git status --porcelain > ../main-status
            cp -- "$bg" "$root/bin/bgtask"
            # Transport for the synthetic ADO URLs goes to local bare repos; `git remote` still reports the configured URLs.
            cat > "$root/bin/git" <<'GIT'
            #!/bin/bash
            root="${HOME%/home}"
            if [[ "${1-}" == remote ]]; then exec /usr/bin/git "$@"; fi
            exec /usr/bin/git -c "url.$root/remote.git.insteadOf=https://dev.azure.com/offline-org/offline-project/_git/offline-repo" \
              -c "url.$root/foreign.git.insteadOf=https://dev.azure.com/foreign-org/foreign-project/_git/foreign-repo" \
              -c "url.$root/sub.git.insteadOf=https://dev.azure.com/offline-org/offline-project/_git/offline-sub" -c protocol.file.allow=always "$@"
            GIT
            cat > "$root/bin/copilot" <<'CLI'
            #!/usr/bin/python3
            import json,os,pathlib,re,shlex,shutil,socket,subprocess,sys
            if "--help" in sys.argv:
                print("--no-ask-user --stream --no-color --deny-tool --yolo --session-id --resume")
                sys.exit(0)
            prompt=sys.stdin.read()
            resumed="--resume" in sys.argv
            session=sys.argv[sys.argv.index("--resume" if resumed else "--session-id")+1]
            state=pathlib.Path(os.environ["COPILOT_HOME"])/"session-state"/session
            state.mkdir(parents=True,exist_ok=True)
            if resumed:
                mode=(state/"fake-mode").read_text()
            else:
                mode="review-gate" if prompt.startswith("Triage Azure DevOps pull request") else prompt.split("\n",1)[0]
                (state/"fake-prompt").write_text(prompt)
                (state/"fake-mode").write_text(mode)
                (state/"events.jsonl").write_text("{}\n")
            (pathlib.Path(os.environ["HOME"])/("launch-"+mode+".json")).write_text(json.dumps({
                "args":sys.argv[1:],"cwd":os.getcwd(),"prompt":prompt,
                "resumed":resumed,"session":session,
                "allowAllEnvironment":os.getenv("COPILOT_ALLOW_ALL"),
                "assistedApproval":os.getenv("COPILOT_ASSISTED_APPROVAL")}))
            listener=socket.socket()
            listener.bind(("127.0.0.1",0))
            listener.listen(1)
            (pathlib.Path(os.environ["HOME"])/("gate-"+mode)).write_text(str(listener.getsockname()[1]))
            head=subprocess.check_output(["git","rev-parse","HEAD"],text=True).strip()
            print("FAKE-READY",os.getcwd(),head,flush=True)
            if resumed: print("FAKE-RESUMED",flush=True)
            print("FAKE-STDERR",file=sys.stderr,flush=True)
            connection,_=listener.accept()
            connection.recv(2)
            connection.close()
            listener.close()
            if mode=="dirty":
                pathlib.Path("agent-change").write_text("agent content\n")
            elif mode=="ignored":
                pathlib.Path("ignored-change").write_text("retained ignored content\n")
            elif mode in ("commit","published"):
                subprocess.check_call(["git","-c","user.name=Offline","-c","user.email=offline@example.invalid","commit","--allow-empty","-qm","generated"])
                if mode=="published":
                    remote=str(pathlib.Path(os.environ["HOME"]).parent/"remote.git")
                    subprocess.check_call(["git","push","--quiet",remote,"HEAD:refs/heads/fix-test"])
            elif mode in ("push-source","push-nonff","push-late","push-sub-unpushed","push-sub-published","push-sub-startup"):
                # Run exactly the publish/verify commands the staged prompt names, as an agent would.
                staged=(state/"fake-prompt").read_text()
                found=re.search(r"publish only with: (.*?) and verify with: (git ls-remote .*?) plus a fresh GET",staged)
                ident=["-c","user.name=Offline","-c","user.email=offline@example.invalid"]
                if mode.startswith("push-sub"):
                    # Initialized submodule on the same branch name as the parent, so inherited on-demand recursion would really push it.
                    branch=subprocess.check_output(["git","symbolic-ref","--short","HEAD"],text=True).strip()
                    subprocess.check_call(["git","submodule","--quiet","update","--init","lib"])
                    if mode=="push-sub-unpushed":
                        subprocess.check_call(["git","-C","lib","checkout","-q","-b",branch])
                        subprocess.check_call(["git","-C","lib"]+ident+["commit","--allow-empty","-qm","unpublished submodule work"])
                    else:
                        subprocess.check_call(["git","-C","lib","checkout","-q","-b",branch,"origin/published"])
                    subprocess.check_call(["git","add","lib"])
                subprocess.check_call(["git","-c","user.name=Offline","-c","user.email=offline@example.invalid","commit","--allow-empty","-qm","fix"])
                head=subprocess.check_output(["git","rev-parse","HEAD"],text=True).strip()
                # A reachable annotated tag absent remotely; push.followTags=true would publish it without --no-follow-tags.
                subprocess.check_call(["git","-c","user.name=Offline","-c","user.email=offline@example.invalid","tag","-a","fix-tag-"+mode,"-m","tag"])
                print("FAKE-TAG",flush=True)
                pushed=subprocess.run(["bash","-c",found.group(1)],stdout=subprocess.DEVNULL,stderr=subprocess.PIPE,text=True)
                rc=pushed.returncode
                print("FAKE-PUSH rc="+str(min(rc,1)),flush=True)
                if "found on any remote" in pushed.stderr: print("FAKE-SUBMODULE-CHECK",flush=True)
                if mode=="push-sub-unpushed":
                    # Control, after the guarded attempt so it cannot influence it: the same push without the check flag, aimed at
                    # disposable copies of both remotes, really publishes the submodule commit under inherited on-demand recursion.
                    root=pathlib.Path(os.environ["HOME"]).parent
                    shutil.copytree(root/"remote.git",root/"control-parent.git"); shutil.copytree(root/"sub.git",root/"control-sub.git")
                    control=[a for a in shlex.split(found.group(1).split("&& ",1)[1]) if a!="--recurse-submodules=check"]
                    subprocess.call(["/usr/bin/git","-c","url."+str(root/"control-parent.git")+".insteadOf=https://dev.azure.com/offline-org/offline-project/_git/offline-repo",
                        "-c","url."+str(root/"control-sub.git")+".insteadOf=https://dev.azure.com/offline-org/offline-project/_git/offline-sub",
                        "-c","protocol.file.allow=always"]+control[1:],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
                    sub_head=subprocess.check_output(["git","-C","lib","rev-parse","HEAD"],text=True).strip()
                    if sub_head in subprocess.check_output(["/usr/bin/git","--git-dir",str(root/"control-sub.git"),"for-each-ref","--format=%(objectname)"],text=True):
                        print("FAKE-CONTROL-RECURSED",flush=True)
                remote=subprocess.check_output(shlex.split(found.group(2)),text=True).split()
                if rc==0 and remote[:1]==[head]: print("FAKE-VERIFIED",flush=True)
            elif mode=="fail":
                sys.exit(9)
            CLI
            chmod +x "$root/bin/copilot" "$root/bin/git" "$root/bin/bgtask"
            printf '%s\n%s\n%s\n%s\n' "$commit" "$second" "$subcommit" "${pushhash%% *}"
            """;
    }
}
