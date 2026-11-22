using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using AdoPrView;

static class MergeChecksTests
{
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("ignored drafts skip attention, detailed reads and merge writes while non-drafts retain classification", async () =>
        {
            using var f = new Fixture();
            f.Store.Current.IgnoreDrafts = true;
            var draft = f.Add(); draft.Draft = true; draft.MergeStatus = "conflicts";
            var normal = f.Add(id: 2); normal.MergeStatus = "conflicts";
            f.FailPrs.Add(draft.Key);
            await f.Service.RefreshAsync(forceMergeCheck: true);
            var view = f.View(draft);
            Check(view.IsIgnoredDraft && view.Error is null && view.Blockers.Count == 0 &&
                view.HumanActions.Count == 0 && view.Gates.Count == 0, "Ignored draft was polled or classified for attention");
            Check(f.View(normal).Lane == Lane.Blocked && !f.View(normal).IsIgnoredDraft, "Non-draft changed");
            Check(f.Patches.Count == 0 && !(await f.Service.RestartMergeAsync(draft.Key)).Requested, "Ignored draft had merge activity");
            try { await f.Service.GetCopilotSourceAsync(draft.Key); throw new Exception("Ignored draft started Fix"); }
            catch (InvalidOperationException ex) { Check(ex.Message.Contains("monitored"), "Fix failed for another reason"); }
        });
        await test("draft ignore defaults off and enabling preserves published and back-to-draft transitions", async () =>
        {
            using var f = new Fixture();
            var p = f.Add(); p.Draft = true;
            var peer = f.Add(id: 2);
            await f.Service.RefreshAsync();
            Check(!f.View(p).IsIgnoredDraft && f.View(p).Lane == Lane.NeedsHuman &&
                f.View(p).HumanActions.Contains("publish"), "Default draft behavior changed");
            f.Store.Current.IgnoreDrafts = true;
            f.Service.ApplyMonitoringSettings();
            Check(f.View(p).IsIgnoredDraft && f.View(p).HumanActions.Count == 0, "Setting did not immediately suppress attention");
            var events = new List<PrView>();
            f.Service.LaneChanged += (pr, _) => events.Add(pr);
            p.Draft = false;
            await f.Service.RefreshAsync();
            Check(!f.View(p).IsIgnoredDraft && f.View(p).Lane == f.View(peer).Lane &&
                !f.View(p).HumanActions.Contains("publish") && f.View(p).Gates.Count > 0, "Publishing did not restore monitoring");
            p.Draft = true; p.MergeStatus = "conflicts";
            await f.Service.RefreshAsync();
            Check(f.View(p).IsIgnoredDraft && events.All(v => !v.IsIgnoredDraft), "Returning to draft notified");
            events.Clear();
            f.FailLists.Add(p.Repo);
            await f.Service.RefreshAsync();
            Check(events.Count == 0, "Failed list emitted draft attention");
            f.FailLists.Clear();
            f.Store.Current.IgnoreDrafts = false;
            f.Service.ApplyMonitoringSettings();
            await f.Service.RefreshAsync();
            Check(!f.View(p).IsIgnoredDraft && f.View(p).Lane == Lane.Blocked, "Turning off ignore did not restore blocked draft");
        });
        await test("merge preflight returning to draft never PATCHes and suppresses cached attention", async () =>
        {
            using var f = new Fixture();
            f.Store.Current.IgnoreDrafts = true;
            var p = f.Add(); p.Preview = Fixture.New;
            f.BeforePrRead = (pr, n) => { if (n == 2) pr.Draft = true; };
            await f.Service.RefreshAsync();
            Check(f.Patches.Count == 0 && f.View(p).IsDraft && f.View(p).IsIgnoredDraft &&
                f.View(p).HumanActions.Count == 0, "Fresh back-to-draft preflight wrote or retained attention");
        });
        await test("draft discovered by fresh row read is excluded before policy polling and actions", async () =>
        {
            using var f = new Fixture();
            f.Store.Current.IgnoreDrafts = true;
            var p = f.Add();
            await f.Service.RefreshAsync();
            p.Draft = true;
            await f.Service.RefreshOneAsync(p.Key);
            Check(f.View(p).IsIgnoredDraft && f.View(p).Gates.Count == 0, "Single-row refresh monitored a draft");
            try { await f.Service.SetAutoCompleteAsync(p.Key, true); throw new Exception("Ignored draft auto-completed"); }
            catch (InvalidOperationException ex) { Check(ex.Message.Contains("ignored"), "Unexpected action error"); }
            Check(f.Patches.Count == 0, "Ignored draft write escaped");
        });
        foreach (var action in new[] { "auto", "cancelAuto", "publish", "requeue" })
            await test($"fresh ignored draft blocks native {action} write", async () =>
            {
                using var f = new Fixture();
                f.Store.Current.IgnoreDrafts = true;
                var p = f.Add();
                await f.Service.RefreshAsync();
                p.Draft = true;
                try
                {
                    switch (action)
                    {
                        case "auto": await f.Service.SetAutoCompleteAsync(p.Key, true); break;
                        case "cancelAuto": await f.Service.SetAutoCompleteAsync(p.Key, false); break;
                        case "publish": await f.Service.PublishAsync(p.Key); break;
                        case "requeue": await f.Service.RequeueAsync(p.Key, "offline-evaluation"); break;
                    }
                    throw new Exception("Ignored draft write accepted");
                }
                catch (InvalidOperationException ex) { Check(ex.Message.Contains("ignored"), "Unexpected action error"); }
                Check(f.Patches.Count == 0, "Ignored draft write escaped");
            });
        await test("merge cadence sends exact restart PATCH, preserves options and follows fresh results", async () =>
        {
            using var f = new Fixture();
            var p = f.Add();
            await f.Service.RefreshAsync();
            Check(f.Patches.Count == 0, "Fresh baseline must not restart");
            p.Preview = Fixture.New;
            f.Clock.Advance(TimeSpan.FromMinutes(9));
            await f.Service.RefreshAsync();
            Check(f.Patches.Count == 0 && f.View(p).MergeStale, "Stale checks ignored cadence");
            Check(!f.View(p).Gates["merge"].Detail!.Contains("queued"), "Claimed queued without a write");
            f.Clock.Advance(TimeSpan.FromMinutes(1));
            await f.Service.RefreshAsync();
            Check(f.Patches.SequenceEqual([p.Key]), "Cadence did not send exactly one selected restart");
            Check(f.View(p).MergeRecheckPending && f.View(p).Gates["merge"].State == "pending", "Accepted restart not visible");
            Check(f.Service.Current.MergeCheckStatus!.Contains("1 requested"), "Requested count missing");
            await f.Service.RefreshAsync();
            Check(f.Patches.Count == 1, "Queued computation restarted");
            f.Complete(p);
            await f.Service.RefreshAsync();
            Check(!f.View(p).MergeStale && !f.View(p).MergeRecheckPending &&
                f.View(p).Gates["merge"].Label == "Clean", "Fresh terminal result was not consumed");
            Check(f.View(p).MergeRecheckRequestedAt is not null, "Last request evidence was lost");
        });

        await test("missing preview commit is stale, not a clean preview that never gets checked", async () =>
        {
            using var f = new Fixture();
            var p = f.Add(); p.Preview = null;
            await f.Service.RefreshAsync();
            Check(f.Patches.SequenceEqual([p.Key]), "Missing preview target did not restart");
        });

        await test("merge trigger followed by authoritative succeeded GET is Clean in the first snapshot without waiting", async () =>
        {
            using var f = new Fixture();
            var p = f.Add(); p.Preview = Fixture.New;
            f.BeforePrRead = (pr, n) => { if (n == 3) f.Complete(pr); };
            await f.Service.RefreshAsync();
            var view = f.View(p);
            Check(f.Patches.SequenceEqual([p.Key]) && view.MergeStatus == "succeeded" && !view.MergeStale &&
                !view.MergeRecheckPending && view.Gates["merge"].Label == "Clean" && view.Lane == Lane.NeedsHuman,
                "Accepted trigger masked its authoritative completed GET result in the first snapshot");
            Check(view.MergeRecheckRequestedAt == f.Clock.GetUtcNow(), "Trigger timestamp was lost");
            await f.Service.RefreshAsync();
            Check(f.Patches.Count == 1, "Completed result invented another merge trigger");
        });

        foreach (var status in new[] { "queued", "notSet" })
            await test($"merge {status} exposes human requirements in the first snapshot and never restarts pending state", async () =>
            {
                using var f = new Fixture();
                var p = f.Add(); p.MergeStatus = status; p.Preview = Fixture.New; p.Draft = true;
                await f.Service.RefreshAsync(forceMergeCheck: true);
                Check(f.View(p).Lane == Lane.NeedsHuman && f.View(p).HumanActions.Contains("publish") &&
                    f.View(p).HumanActions.Contains("autocomplete") && f.View(p).Waiting.Count == 0 &&
                    f.View(p).Gates["merge"].Label == (status == "queued" ? "Queued" : "Unknown"),
                    "First pending snapshot hid available human actions");
                var manual = await f.Service.RestartMergeAsync(p.Key);
                f.Clock.Advance(TimeSpan.FromHours(1));
                await f.Service.RefreshAsync(forceMergeCheck: true);
                Check(!manual.Requested && f.Patches.Count == 0, "Pending/unset merge was cancelled or restarted");
            });

        foreach (var incomplete in new[] { false, true })
        await test($"accepted merge trigger with failed or incomplete follow-up GET reports read error without retrying early (incomplete={incomplete})", async () =>
        {
            using var f = new Fixture();
            var p = f.Add(); p.Preview = Fixture.New; f.ReturnQueued = false;
            f.BeforePrRead = (pr, n) =>
            {
                if (n != 3) return;
                if (incomplete) pr.Status = "";
                else f.FailPrs.Add(pr.Key);
            };
            await f.Service.RefreshAsync();
            Check(f.Patches.Count == 1 && f.View(p).MergeRecheckRequestedAt == f.Clock.GetUtcNow() &&
                f.View(p).MergeRecheckError is null && f.View(p).MergeCheckError!.Contains("was accepted") &&
                f.View(p).Gates["merge"].State == "warn" && f.Service.Current.MergeCheckStatus!.Contains("1 requested"),
                "Failed post-trigger read became a failed trigger or a clean preview");
            f.FailPrs.Clear(); f.BeforePrRead = null; p.Status = "active";
            f.Clock.Advance(TimeSpan.FromSeconds(120));
            await f.Service.RefreshAsync();
            Check(f.Patches.Count == 1 && f.View(p).MergeCheckError is null && f.View(p).Gates["merge"].Label == "Stale",
                "Read-back recovery eagerly duplicated an accepted trigger");
            f.Clock.Advance(TimeSpan.FromMinutes(8));
            await f.Service.RefreshAsync();
            Check(f.Patches.Count == 2 && f.View(p).Gates["merge"].Label == "Stale", "Accepted trigger did not retain retry cadence");
        });

        foreach (var missing in new[] { false, true })
            await test($"merge target {(missing ? "missing" : "read failure")} is explicit and recovery retries before the next cadence", async () =>
            {
                using var f = new Fixture();
                var p = f.Add(); p.Preview = Fixture.New;
                f.HeadFailure = missing ? HttpStatusCode.OK : HttpStatusCode.Forbidden;
                await f.Service.RefreshAsync();
                Check(f.Patches.Count == 0 && f.View(p).MergeCheckError is not null, "Unverified target silently accepted");
                Check(f.View(p).Gates["merge"].Label == "Unverified" && f.Service.Current.RepoErrors.Count > 0, "Target error not visible");
                f.HeadFailure = null;
                f.Clock.Advance(TimeSpan.FromSeconds(120));
                await f.Service.RefreshAsync();
                Check(f.Patches.SequenceEqual([p.Key]), "Detection recovery incorrectly waited another merge interval");
            });

        await test("forced merge sweep includes formerly clean previews and shares one exact ref read", async () =>
        {
            using var f = new Fixture();
            var a = f.Add(); var b = f.Add(id: 2);
            await f.Service.RefreshAsync();
            var reads = f.HeadReads;
            await f.Service.RefreshAsync(forceMergeCheck: true);
            Check(f.Patches.Order().SequenceEqual(new[] { a.Key, b.Key }.Order()), "Forced sweep was restricted to stale rows");
            Check(f.HeadReads == reads + 1, "Same target fetched more than once in a refresh");
            Check(f.PatchBodies.All(body => body.AsObject().Count == 1 &&
                JsonNode.DeepEquals(body["mergeOptions"], Fixture.Options)), "Restart changed merge or completion options");
        });

        await test("conflicts skip elapsed-cadence stale rechecks while eligible peers still restart", async () =>
        {
            using var f = new Fixture();
            var conflict = f.Add(); conflict.MergeStatus = "conflicts";
            var peer = f.Add(id: 2);
            await f.Service.RefreshAsync();
            conflict.Preview = peer.Preview = Fixture.New;
            f.Clock.Advance(TimeSpan.FromMinutes(10));
            await f.Service.RefreshAsync();
            Check(f.Patches.SequenceEqual([peer.Key]), "Elapsed cadence restarted conflicts or skipped an eligible peer");
            Check(f.View(conflict).MergeStale && !f.View(conflict).MergeRecheckPending &&
                f.View(conflict).MergeRecheckRequestedAt is null && f.View(conflict).Gates["merge"].Label == "Conflicts",
                "Conflicts lost real staleness or claimed a queued restart");
        });

        foreach (var force in new[] { false, true })
            await test($"conflicts skip target/source advancement and forced sweeps without deferred retry (force={force})", async () =>
            {
                using var f = new Fixture();
                var conflict = f.Add(); conflict.MergeStatus = "conflicts";
                var peer = f.Add(id: 2); var other = f.Add("repo-two");
                await f.Service.RefreshAsync();
                f.Heads[conflict.Repo] = Fixture.New;
                conflict.Source = Fixture.New;
                peer.Preview = Fixture.New;
                await f.Service.RefreshAsync(forceMergeCheck: force);
                Check(f.Patches.Order().SequenceEqual(new[] { peer.Key, other.Key }.Order()), "A sweep restarted conflicts or missed eligible peers");
                await f.Service.RefreshOneAsync(conflict.Key);
                f.Complete(peer); f.Complete(other);
                f.Clock.Advance(TimeSpan.FromHours(1));
                await f.Service.RefreshAsync(forceMergeCheck: true);
                Check(!f.Patches.Contains(conflict.Key) && !f.View(conflict).MergeRecheckPending &&
                    f.View(conflict).Gates["merge"].Label == "Conflicts", "Conflicts accumulated a pending/retry restart");
                f.Complete(peer); f.Complete(other);
                conflict.MergeStatus = "queued";
                await f.Service.RefreshOneAsync(conflict.Key);
                Check(f.View(conflict).MergeStatus == "queued" && !f.Patches.Contains(conflict.Key),
                    "Read-only refresh did not observe source-push-driven recomputation");
                f.Complete(conflict);
                await f.Service.RefreshOneAsync(conflict.Key);
                await f.Service.RefreshAsync();
                Check(f.View(conflict).Gates["merge"].Label == "Clean" && !f.Patches.Contains(conflict.Key),
                    "Conflict exclusion replayed an old sweep after the push resolved conflicts");
                conflict.Preview = Fixture.Old;
                f.Clock.Advance(TimeSpan.FromMinutes(10));
                await f.Service.RefreshAsync();
                Check(f.Patches.Count(k => k == conflict.Key) == 1, "Ordinary eligibility did not resume after ADO reported non-conflict");
            });

        await test("a failed restart followed by conflicts retains diagnostics but pauses every automatic retry", async () =>
        {
            using var f = new Fixture();
            var p = f.Add(); p.Preview = Fixture.New;
            f.PatchFailure = HttpStatusCode.ServiceUnavailable;
            await f.Service.RefreshAsync();
            p.MergeStatus = "conflicts"; f.PatchFailure = null;
            f.Clock.Advance(TimeSpan.FromHours(1));
            await f.Service.RefreshOneAsync(p.Key);
            await f.Service.RefreshAsync(forceMergeCheck: true);
            await f.Service.RefreshAsync();
            var view = f.View(p);
            Check(f.Patches.SequenceEqual([p.Key]) && !view.MergeRecheckPending && view.Gates["merge"].Label == "Conflicts",
                "Conflict kept an automatic retry alive or was shown as checking");
            Check(view.MergeRecheckError!.Contains("503") && view.MergeRecheckError.Contains("paused") &&
                !view.MergeRecheckError.Contains("Retrying"), "Genuine error was erased or still promises automatic retries during conflicts");
        });

        await test("conflicts replace an accepted stale result but a deliberate manual recheck remains available", async () =>
        {
            using var f = new Fixture();
            var p = f.Add(); p.Preview = Fixture.New; f.ReturnQueued = false;
            await f.Service.RefreshAsync();
            Check(!f.View(p).MergeRecheckPending && f.View(p).Gates["merge"].Label == "Stale", "Accepted stale result invented pending state");
            p.MergeStatus = "conflicts";
            await f.Service.RefreshOneAsync(p.Key);
            await f.Service.RefreshAsync(forceMergeCheck: true);
            Check(f.Patches.Count == 1 && !f.View(p).MergeRecheckPending && f.View(p).Gates["merge"].Label == "Conflicts",
                "Observed conflicts retained a stale pending restart");
            f.ReturnQueued = true;
            var result = await f.Service.RestartMergeAsync(p.Key);
            Check(result.Requested && f.Patches.SequenceEqual(new[] { p.Key, p.Key }) &&
                f.View(p).MergeRecheckPending, "Conflict exclusion incorrectly blocked the explicit manual command");
        });

        await test("target advancement coalesces a burst into one cross-repo sweep, unchanged refs do not repeat", async () =>
        {
            using var f = new Fixture();
            var a = f.Add(); var b = f.Add("repo-two"); var c = f.Add(id: 3);
            await f.Service.RefreshAsync();
            f.Heads["repo-one"] = Fixture.New;
            f.Heads["repo-two"] = Fixture.New;
            a.Preview = b.Preview = c.Preview = Fixture.New; // Cached previews may still say clean.
            await f.Service.RefreshAsync();
            Check(f.Patches.Order().SequenceEqual(new[] { a.Key, b.Key, c.Key }.Order()), "Target movement did not force every monitored preview exactly once");
            Check(f.Service.Current.MergeCheckStatus!.Contains("target branch advanced"), "Sweep reason not observable");
            foreach (var p in new[] { a, b, c }) f.Complete(p);
            await f.Service.RefreshAsync();
            await f.Service.RefreshAsync();
            Check(f.Patches.Count == 3, "Unchanged target refs caused a restart loop");
        });

        await test("last watched PR completion triggers remaining previews through target advancement, not row disappearance", async () =>
        {
            using var f = new Fixture();
            var merged = f.Add(); var survivor = f.Add("repo-two");
            await f.Service.RefreshAsync();
            merged.Status = "completed";
            f.Heads["repo-one"] = Fixture.New;
            await f.Service.RefreshAsync();
            Check(f.Patches.SequenceEqual([survivor.Key]), "Merge of last watched PR did not refresh the other repo");
            Check(f.Service.Current.Prs.All(p => p.Key != merged.Key), "Completed PR retained");
        });

        await test("hides, lane changes, filters, missing rows and failed/incomplete refreshes cannot invent a merge", async () =>
        {
            using var f = new Fixture();
            var a = f.Add(); var b = f.Add("repo-two");
            await f.Service.RefreshAsync();
            a.Draft = true;
            await f.Service.RefreshAsync(); // Awaiting completion -> Needs Human, with unchanged target.
            f.Store.Update(s => s.Hidden.Add(a.Key));
            await f.Service.RefreshAsync();
            f.FailLists.Add("repo-two");
            await f.Service.RefreshAsync();
            f.FailLists.Clear();
            b.Author = "not-watched";
            await f.Service.RefreshAsync();
            f.Store.Update(s => s.Repos.RemoveAll(r => r.Repo == "repo-two"));
            await f.Service.RefreshAsync();
            Check(f.Patches.Count == 0, "UI visibility or missing/error rows were treated as merges");
        });

        await test("hidden and error PRs are never restarted, while force work survives incomplete repo refresh", async () =>
        {
            using var f = new Fixture();
            var a = f.Add(); var hidden = f.Add(id: 2); var bad = f.Add(id: 3); var other = f.Add("repo-two");
            f.Store.Update(s => s.Hidden.Add(hidden.Key));
            await f.Service.RefreshAsync();
            f.FailPrs.Add(bad.Key); f.FailLists.Add("repo-two");
            f.Heads["repo-one"] = Fixture.New;
            await f.Service.RefreshAsync();
            Check(f.Patches.SequenceEqual([a.Key]), "Hidden/error rows included in a sweep");
            f.FailPrs.Clear(); f.FailLists.Clear();
            await f.Service.RefreshAsync();
            Check(f.Patches.Order().SequenceEqual(new[] { a.Key, bad.Key, other.Key }.Order()), "Deferred forced work lost on failure");
            Check(!f.Patches.Contains(hidden.Key), "Hidden PR restarted during recovery");
        });

        foreach (var status in new[] { "queued", "notSet" })
            await test($"in-flight {status} previews are not cancelled by periodic or forced sweeps", async () =>
            {
                using var f = new Fixture();
                var p = f.Add(); p.Preview = Fixture.New; p.MergeStatus = status;
                await f.Service.RefreshAsync(forceMergeCheck: true);
                var manual = await f.Service.RestartMergeAsync(p.Key);
                f.Clock.Advance(TimeSpan.FromHours(1));
                await f.Service.RefreshAsync();
                Check(f.Patches.Count == 0 && !manual.Requested && manual.Message.Contains("not restarted"), "In-flight job was cancelled or falsely reported requested");
                f.Complete(p);
                await f.Service.RefreshAsync();
                Check(f.Patches.SequenceEqual([p.Key]), "Deferred force sweep was lost");
            });

        await test("fresh preflight protects a PR that queued, conflicted or completed after the list/build read", async () =>
        {
            foreach (var state in new[] { "queued", "conflicts", "completed" })
            {
                using var f = new Fixture();
                var p = f.Add(); p.Preview = Fixture.New;
                f.BeforePrRead = (pr, n) =>
                {
                    if (n != 2) return;
                    if (state == "completed") pr.Status = state;
                    else pr.MergeStatus = state;
                };
                await f.Service.RefreshAsync();
                Check(f.Patches.Count == 0 && f.Service.Current.MergeCheckStatus!.Contains("0 requested"), "Preflight race sent a restart or claimed success");
                if (state == "completed") Check(f.Service.Current.Prs.Count == 0, "Completed PR not removed");
                if (state == "conflicts") Check(!f.View(p).MergeRecheckPending && f.View(p).Gates["merge"].Label == "Conflicts",
                    "Fresh conflict was displayed as a queued restart");
            }
        });

        await test("failed restart is not transport-retried, remains visible, and retries on refresh rather than waiting ten minutes", async () =>
        {
            using var f = new Fixture();
            var p = f.Add(); p.Preview = Fixture.New;
            f.PatchFailure = HttpStatusCode.ServiceUnavailable;
            await f.Service.RefreshAsync();
            Check(f.Patches.SequenceEqual([p.Key]), "Write had an automatic HTTP retry");
            Check(f.View(p).MergeRecheckError!.Contains("503") && !f.View(p).MergeRecheckPending, "Failure swallowed or reported as queued");
            Check(f.View(p).Gates["merge"].Label == "Retry needed" && f.Service.Current.MergeCheckStatus!.Contains("0 requested"), "Failure feedback incorrect");
            await f.Service.RefreshAsync();
            Check(f.Patches.Count == 1, "Retry backoff ignored");
            f.PatchFailure = null;
            f.Clock.Advance(TimeSpan.FromSeconds(120));
            await f.Service.RefreshAsync();
            Check(f.Patches.Count == 2 && f.View(p).MergeRecheckError is null && f.View(p).MergeRecheckPending, "Refresh did not retry/clear error");
        });

        await test("failed force sweep retries even when preview was clean; another successful row is not repeated", async () =>
        {
            using var f = new Fixture();
            var a = f.Add(); var b = f.Add(id: 2);
            await f.Service.RefreshAsync();
            f.PatchFailureKey = a.Key; f.PatchFailure = HttpStatusCode.Forbidden;
            await f.Service.RefreshAsync(forceMergeCheck: true);
            f.Complete(b);
            f.PatchFailure = null; f.Clock.Advance(TimeSpan.FromSeconds(120));
            await f.Service.RefreshAsync();
            Check(f.Patches.Count(k => k == a.Key) == 2 && f.Patches.Count(k => k == b.Key) == 1, "Force retry lost or healthy PR repeated");
        });

        await test("accepted stale merge results expose human-only requirements immediately and retain nonduplicative cadence", async () =>
        {
            using var f = new Fixture();
            var p = f.Add(); p.Preview = Fixture.New;
            f.ReturnQueued = false;
            foreach (var (name, status) in new[] { ("Build", "approved"), ("Minimum reviewers", "approved"),
                ("Status", "approved"), ("Work item linking", "approved"), ("Proof of Presence", "rejected"),
                ("Merge strategy", "rejected") })
                f.Policies.Add(new JsonObject
                {
                    ["evaluationId"] = name, ["status"] = status,
                    ["configuration"] = new JsonObject { ["isBlocking"] = true,
                        ["type"] = new JsonObject { ["id"] = "offline", ["displayName"] = name } },
                });
            await f.Service.RefreshAsync();
            Check(!f.View(p).MergeRecheckPending && f.View(p).Lane == Lane.NeedsHuman &&
                f.View(p).Gates["merge"].Label == "Stale", "Initial accepted stale result hid human attention");
            for (var minutes = 2; minutes <= 40; minutes += 2)
            {
                f.Clock.Advance(TimeSpan.FromSeconds(120));
                await f.Service.RefreshAsync();
                var view = f.View(p);
                Check(f.Patches.Count == 1 + minutes / 10, "Stale retries duplicated writes or lost their cadence");
                Check(view.MergeStale, "Old terminal result was called current");
                Check(!view.MergeRecheckPending && view.Gates["merge"].State == "warn" &&
                    view.Gates["merge"].Label == "Stale" && view.MergeRecheckError is null,
                    "Repeated stale responses invented pending work or trigger failures");
                Check(view.Lane == Lane.NeedsHuman && view.Waiting.Count == 0 && view.Blockers.Count == 0 &&
                    view.HumanActions.Order().SequenceEqual(new[] { "autocomplete", "pop" }), "Stale bookkeeping hid human-only requirements");
                Check(f.Service.Current.MergeCheckStatus!.Contains("0 queued"), "Status summary invented running work");
            }
            var requestedAt = f.View(p).MergeRecheckRequestedAt;
            Check(requestedAt == f.Clock.GetUtcNow(), "Successful retry request timestamp was not retained");
            f.Complete(p);
            await f.Service.RefreshAsync();
            Check(f.Patches.Count == 5 && !f.View(p).MergeRecheckPending && !f.View(p).MergeStale &&
                f.View(p).MergeRecheckError is null && f.View(p).Gates["merge"].Label == "Clean" &&
                f.View(p).MergeRecheckRequestedAt == requestedAt, "Late fresh result did not clear the warning or retain request evidence");
        });

        foreach (var status in new[] { "queued", "notSet" })
            await test($"accepted merge retry observes {status} without hiding human actions or permitting cancellation", async () =>
            {
                using var f = new Fixture();
                var p = f.Add(); p.Preview = Fixture.New; f.ReturnQueued = false;
                await f.Service.RefreshAsync();
                f.ReturnQueued = true;
                f.Clock.Advance(TimeSpan.FromMinutes(10));
                await f.Service.RefreshAsync();
                p.MergeStatus = status;
                for (var i = 0; i < 3; i++)
                {
                    f.Clock.Advance(TimeSpan.FromMinutes(10));
                    await f.Service.RefreshAsync();
                    var manual = await f.Service.RestartMergeAsync(p.Key);
                    Check(f.Patches.Count == 2 && !manual.Requested && f.View(p).MergeRecheckPending == (status == "queued") &&
                        f.View(p).Gates["merge"].Label == (status == "queued" ? "Queued" : "Unknown") && f.View(p).Lane == Lane.NeedsHuman,
                        "Observed in-flight retry was timed out, promoted or restarted");
                }
                f.Complete(p);
                await f.Service.RefreshAsync();
                Check(f.Patches.Count == 2 && !f.View(p).MergeRecheckPending &&
                    f.View(p).MergeRecheckError is null && f.View(p).Gates["merge"].Label == "Clean", "Actual completed retry was not consumed");
            });

        foreach (var (status, label) in new[] { ("failure", "Failed"), ("rejectedByPolicy", "Rejected"), ("conflicts", "Conflicts") })
            await test($"terminal merge {status} replaces local pending even with a stale target and another accepted restart", async () =>
            {
                using var f = new Fixture();
                var p = f.Add(); p.Preview = Fixture.New; f.ReturnQueued = false;
                await f.Service.RefreshAsync();
                p.MergeStatus = status; p.MergeFailure = "Offline merge failure";
                f.Clock.Advance(TimeSpan.FromSeconds(120));
                await f.Service.RefreshOneAsync(p.Key);
                Check(!f.View(p).MergeRecheckPending && f.View(p).Lane == Lane.Blocked &&
                    f.View(p).Gates["merge"].Label == label, "Fresh terminal problem was hidden by local request grace");
                await f.Service.RefreshAsync(forceMergeCheck: true);
                Check(f.Patches.Count == (status == "conflicts" ? 1 : 2) && !f.View(p).MergeRecheckPending &&
                    f.View(p).Lane == Lane.Blocked && f.View(p).Gates["merge"].Label == label,
                    "Accepted terminal restart response masked failure or automatically restarted conflicts");
                if (status == "failure")
                    Check(f.View(p).Gates["merge"].Detail!.Contains(p.MergeFailure), "Merge failure explanation was lost");
            });

        await test("stale merge result retains target read diagnostics until a verified completed preview arrives", async () =>
        {
            using var f = new Fixture();
            var p = f.Add(); p.Preview = Fixture.New; f.ReturnQueued = false;
            await f.Service.RefreshAsync();
            f.HeadFailure = HttpStatusCode.Forbidden;
            f.Clock.Advance(TimeSpan.FromMinutes(10));
            await f.Service.RefreshAsync();
            Check(!f.View(p).MergeRecheckPending && f.View(p).Gates["merge"].State == "warn" &&
                f.View(p).Gates["merge"].Detail!.Contains("Cannot verify merge target") &&
                f.View(p).MergeRecheckError is null && f.Service.Current.RepoErrors.Count > 0,
                "Unverified terminal retry hid target errors or claimed it was running");
            f.HeadFailure = null; f.Complete(p);
            await f.Service.RefreshOneAsync(p.Key);
            Check(!f.View(p).MergeRecheckPending && f.View(p).MergeCheckError is null &&
                f.View(p).MergeRecheckError is null && f.View(p).Gates["merge"].Label == "Clean",
                "Verified late result did not clear only the expired-preview warning");
        });

        foreach (var (status, label) in new[] { ("failure", "Failed"), ("rejectedByPolicy", "Rejected"), ("conflicts", "Conflicts") })
            foreach (var restartFailed in new[] { false, true })
                await test($"verified terminal merge {status} retains only genuine restart error={restartFailed}", async () =>
                {
                    using var f = new Fixture();
                    var p = f.Add(); p.Preview = Fixture.New; f.ReturnQueued = false;
                    await f.Service.RefreshAsync();
                    f.Clock.Advance(TimeSpan.FromMinutes(10));
                    if (restartFailed) f.PatchFailure = HttpStatusCode.ServiceUnavailable;
                    await f.Service.RefreshAsync();
                    f.PatchFailure = null;
                    f.Complete(p); p.MergeStatus = status; p.MergeFailure = "Offline terminal failure";
                    var requestedAt = f.View(p).MergeRecheckRequestedAt;
                    await f.Service.RefreshOneAsync(p.Key);
                    var view = f.View(p);
                    Check(!view.MergeStale && !view.MergeRecheckPending && view.Lane == Lane.Blocked &&
                        view.Gates["merge"].Label == label, "Verified terminal result lost its actual merge state");
                    Check(view.MergeRecheckError is null || !view.MergeRecheckError.Contains("current target"),
                        "Verified terminal result retained the expired preview warning");
                    Check(restartFailed ? view.MergeRecheckError?.Contains("503") == true : view.MergeRecheckError is null,
                        "Timeout cleanup erased a transport error or retained a timeout-only error");
                    Check(view.MergeRecheckRequestedAt == requestedAt, "Terminal cleanup lost request evidence");
                    await f.Service.RefreshAsync();
                    Check(f.Patches.Count == 2 && f.Service.Current.MergeCheckStatus!.Contains(
                        $"{(restartFailed ? 1 : 0)} with re-check/target errors"),
                        "Terminal cleanup retained a false summary error or duplicated a retry");
                });

        await test("failed merge retry followed by accepted stale retries retains warnings and cadence without hiding human work", async () =>
        {
            using var f = new Fixture();
            var p = f.Add(); p.Preview = Fixture.New; f.ReturnQueued = false;
            await f.Service.RefreshAsync();
            f.Clock.Advance(TimeSpan.FromMinutes(10));
            f.PatchFailure = HttpStatusCode.ServiceUnavailable;
            await f.Service.RefreshAsync();
            Check(!f.View(p).MergeRecheckPending && f.View(p).MergeRecheckError!.Contains("503") &&
                f.View(p).MergeStale, "Failed retry discarded the observed stale result");
            await f.Service.RefreshAsync();
            Check(f.Patches.Count == 2, "Transport failure backoff was ignored");
            f.PatchFailure = null;
            f.Clock.Advance(TimeSpan.FromSeconds(120));
            await f.Service.RefreshAsync();
            for (var minutes = 12; minutes <= 32; minutes += 2)
            {
                var view = f.View(p);
                Check(f.Patches.Count == 3 + (minutes - 12) / 10, "Accepted stale retries lost their per-request cadence");
                Check(!view.MergeRecheckPending && view.MergeStale && view.Gates["merge"].State == "warn" &&
                    view.Lane == Lane.NeedsHuman && view.Waiting.Count == 0, "Transport recovery renewed expired Checking");
                Check(view.MergeRecheckError is null && view.Gates["merge"].Label == "Stale",
                    "Accepted retry lost staleness or retained a recovered transport error");
                if (minutes == 32) break;
                f.Clock.Advance(TimeSpan.FromSeconds(120));
                await f.Service.RefreshAsync();
            }
            var requestedAt = f.View(p).MergeRecheckRequestedAt;
            f.Complete(p);
            await f.Service.RefreshAsync();
            Check(f.Patches.Count == 5 && f.View(p).MergeRecheckError is null &&
                !f.View(p).MergeRecheckPending && f.View(p).Gates["merge"].Label == "Clean" &&
                f.View(p).MergeRecheckRequestedAt == requestedAt, "Late verified result did not consume independent preview expiry");
        });

        await test("target read failure cannot erase the baseline or hide later external target movement", async () =>
        {
            using var f = new Fixture();
            var a = f.Add(); var b = f.Add("repo-two");
            await f.Service.RefreshAsync();
            f.HeadFailure = HttpStatusCode.Forbidden;
            await f.Service.RefreshAsync();
            Check(f.Patches.Count == 0, "Ref errors triggered a merge sweep");
            f.Heads["repo-one"] = Fixture.New;
            a.Preview = Fixture.New; f.HeadFailure = null;
            await f.Service.RefreshAsync();
            Check(f.Patches.Order().SequenceEqual(new[] { a.Key, b.Key }.Order()), "Target baseline lost across error");
        });

        await test("manual and scheduled restarts serialize and never duplicate an outstanding preview", async () =>
        {
            using var f = new Fixture();
            var p = f.Add();
            await f.Service.RefreshAsync();
            f.HoldPatch = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var sweep = f.Service.RefreshAsync(forceMergeCheck: true);
            await f.PatchEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var manual = f.Service.RestartMergeAsync(p.Key);
            f.HoldPatch.SetResult();
            await sweep;
            var result = await manual;
            Check(f.Patches.SequenceEqual([p.Key]) && !result.Requested, "Manual action duplicated scheduled restart");
        });

        await test("only explicit refresh marks snapshots for a reorder; row updates and merge sweeps do not", async () =>
        {
            using var f = new Fixture();
            var p = f.Add();
            await f.Service.RefreshAsync();
            await f.Service.RefreshOneAsync(p.Key);
            await f.Service.RefreshAsync(forceMergeCheck: true);
            Check(f.Service.Current.OrderRevision == 0, "Automatic refresh/merge sweep reordered the UI");
            f.Complete(p);
            await f.Service.RefreshAsync(reorder: true);
            Check(f.Service.Current.OrderRevision == 1, "Explicit refresh did not authorize a reorder");
            await f.Service.RefreshOneAsync(p.Key);
            await f.Service.RefreshAsync();
            Check(f.Service.Current.OrderRevision == 1, "Background/single-row updates changed the reorder revision");
        });

        await test("several target advances during an in-flight job coalesce into one deferred sweep at the latest head", async () =>
        {
            using var f = new Fixture();
            var p = f.Add();
            await f.Service.RefreshAsync();
            f.Heads[p.Repo] = Fixture.New;
            await f.Service.RefreshAsync();
            f.Heads[p.Repo] = new string('c', 40);
            await f.Service.RefreshAsync();
            f.Heads[p.Repo] = new string('d', 40);
            await f.Service.RefreshAsync();
            Check(f.Patches.Count == 1, "Target burst cancelled an in-flight preview");
            f.Complete(p);
            await f.Service.RefreshAsync();
            Check(f.Patches.Count == 2, "Deferred latest-head sweep missing");
            f.Complete(p);
            await f.Service.RefreshAsync();
            Check(f.Patches.Count == 2 && !f.View(p).MergeRecheckPending, "Burst caused repeated sweep generations on unchanged refs");
        });

        await test("fresh recomputed conflicts replace pending state and report the actual blocked result", async () =>
        {
            using var f = new Fixture();
            var p = f.Add(); p.Preview = Fixture.New;
            await f.Service.RefreshAsync();
            f.Complete(p); p.MergeStatus = "conflicts";
            await f.Service.RefreshAsync();
            Check(!f.View(p).MergeRecheckPending && f.View(p).Lane == Lane.Blocked &&
                f.View(p).Gates["merge"].Label == "Conflicts", "Recomputed conflicts hidden by optimistic pending/clean state");
            await f.Service.RefreshOneAsync(p.Key);
            Check(f.View(p).Blockers.Count(x => x.Contains("Merge conflicts")) == 1, "Reclassification duplicated blockers");
        });

        await test("completed history: a capped raw page with no watched match still warns and stays apart from active PRs", async () =>
        {
            using var f = new Fixture();
            var active = f.Add();
            f.CompletedNoise = PrService.CompletedTop;
            await f.Service.RefreshAsync();
            var s = f.Service.Current;
            Check(s.Completed.Count == 0 && s.CompletedWarnings.SequenceEqual(new[] { "repo-one: Checked 100 completions in this repository; watched PRs may be missing." }),
                "Capped history without watched matches did not warn: " + string.Join("|", s.CompletedWarnings));
            var q = f.CompletedQueries.Single();
            Check(q.Contains("searchCriteria.queryTimeRangeType=closed") && q.Contains("searchCriteria.minTime=2026-10-25T00:00:00Z") && q.Contains("$top=100"),
                "Wrong completed-history query: " + q);
            Check(s.Prs.Single().Key == active.Key && s.Error is null && s.RepoErrors.Count == 0, "History cap affected active refresh");
            f.CompletedNoise = PrService.CompletedTop - 1;
            await f.Service.RefreshAsync();
            Check(f.Service.Current.CompletedWarnings.Count == 0, "Uncapped page warned");
        });

        await test("completed history: watched in-window completions sort by closed date; abandoned, old and unwatched never appear", async () =>
        {
            using var f = new Fixture();
            f.Store.Current.Whitelist.Add("Friend");
            PullRequest Done(int id, string closed, string author = "me", string status = "completed")
            {
                var p = f.Add(id: id); p.Status = status; p.Closed = closed; p.Author = author; return p;
            }
            var old = Done(2, "2026-10-20T00:00:00Z");
            var newest = Done(3, "2026-10-31T12:00:00Z");
            var hidden = Done(4, "2026-10-27T00:00:00Z");
            var abandoned = Done(5, "2026-10-30T00:00:00Z", status: "abandoned");
            var stranger = Done(6, "2026-10-30T00:00:00Z", author: "stranger");
            var friend = Done(7, "2026-10-29T00:00:00Z", author: "Friend");
            f.Store.Current.Hidden.Add(hidden.Key);
            await f.Service.RefreshAsync();
            var c = f.Service.Current.Completed;
            Check(c.Select(x => x.Key).SequenceEqual(new[] { newest.Key, friend.Key, hidden.Key }), "Wrong history rows/order: " + string.Join(",", c.Select(x => x.Key)));
            Check(c[0].IsMine && !c[1].IsMine && c[2].IsHidden && !c[0].IsHidden && c.All(x => x.ClosedBy is null), "History lost mine/hidden or invented closedBy");
            Check(f.Service.Current.Prs.Count == 0, "History entered active PRs");
            try { f.Service.Find(newest.Key); throw new Exception("Completed PR became actionable"); }
            catch (InvalidOperationException) { }
        });

        await test("completed history: obtained completed detail moves a PR exactly once; disappearance and abandonment never do", async () =>
        {
            using var f = new Fixture();
            var p = f.Add(); var gone = f.Add(id: 2); var abandoned = f.Add(id: 3); var during = f.Add(id: 4);
            await f.Service.RefreshAsync();
            p.Status = "completed"; p.Closed = "2026-10-31T00:00:00Z";
            await f.Service.RefreshOneAsync(p.Key);
            var s = f.Service.Current;
            Check(s.Prs.All(x => x.Key != p.Key) && s.Completed.Single().Key == p.Key, "Detail completion did not move into history");
            Check(!System.Text.Json.JsonSerializer.Serialize(s.Prs[0]).Contains("CompletedAt"), "Internal completion evidence changed the PR wire type");
            abandoned.Status = "abandoned"; abandoned.Closed = "2026-10-31T00:00:00Z";
            await f.Service.RefreshOneAsync(abandoned.Key);
            Check(f.Service.Current.Completed.Single().Key == p.Key && f.Service.Current.Prs.All(x => x.Key != abandoned.Key), "Abandoned PR entered history");
            gone.Status = "completed"; // No dated completion evidence: it only disappears.
            f.BeforePrRead = (pr, _) => { if (pr == during) { during.Status = "completed"; during.Closed = "2026-10-31T06:00:00Z"; } };
            var snaps = new List<Snapshot>();
            f.Service.Changed += snaps.Add;
            await f.Service.RefreshAsync();
            Check(snaps[0].Refreshing && snaps[0].Completed.Single().Key == p.Key, "Refreshing snapshot dropped history");
            var keys = f.Service.Current.Completed.Select(x => x.Key).ToList();
            Check(keys.SequenceEqual(new[] { during.Key, p.Key }), "History duplicated, missed a detail move, or inferred completion: " + string.Join(",", keys));
            Check(f.Service.Current.Prs.All(x => x.Key != during.Key && x.Key != gone.Key), "Completed PR stayed active");
        });

        foreach (var (body, status) in new[] { ("""{"message":"denied"}""", HttpStatusCode.Forbidden), ("{}", HttpStatusCode.OK), ("", HttpStatusCode.OK), ("""{"value":{}}""", HttpStatusCode.OK) })
            await test("completed history unavailable (" + status + " " + body + ") warns without harming active PRs", async () =>
            {
                using var f = new Fixture();
                var p = f.Add();
                f.CompletedResponse = (body, status);
                await f.Service.RefreshAsync();
                var s = f.Service.Current;
                Check(s.Error is null && s.RepoErrors.Count == 0 && s.Prs.Single().Key == p.Key && s.Prs[0].Error is null, "History failure harmed active results");
                Check(s.Completed.Count == 0 && s.CompletedWarnings.Single().StartsWith("repo-one: Completed history unavailable"), "History failure was hidden as an empty success");
            });

        await test("completed history: dated terminal detail wins even when active-only enrichment fails or the PR is an ignored draft", async () =>
        {
            using var f = new Fixture();
            f.Store.Current.IgnoreDrafts = true;
            var p = f.Add(); var draft = f.Add(id: 2); draft.Draft = true; var live = f.Add(id: 3);
            f.CompletedResponse = ("""{"value":[]}""", HttpStatusCode.OK);
            f.FailEnrichment = true;
            // Listed as active, then the detail read sees a dated completion.
            f.BeforePrRead = (pr, _) => { if (pr == p) { pr.Status = "completed"; pr.Closed = "2026-10-31T00:00:00Z"; } };
            await f.Service.RefreshAsync();
            var s = f.Service.Current;
            Check(s.Completed.Single().Key == p.Key && s.Prs.All(x => x.Key != p.Key) && s.Prs.Any(x => x.Key == live.Key),
                "Terminal detail fell back to an active row: " + string.Join(",", s.Prs.Select(x => x.Key)));
            Check(f.View(live).Error is not null, "Fixture did not fail active enrichment");
            try { f.Service.Find(p.Key); throw new Exception("Completed PR stayed actionable"); } catch (InvalidOperationException) { }
            f.BeforePrRead = null;
            live.Status = "completed"; live.Closed = "2026-10-31T01:00:00Z";
            await f.Service.RefreshOneAsync(live.Key);
            draft.Status = "completed"; draft.Closed = "2026-10-31T02:00:00Z";
            await f.Service.RefreshOneAsync(draft.Key);
            var keys = f.Service.Current.Completed.Select(x => x.Key).ToList();
            Check(keys.SequenceEqual(new[] { draft.Key, live.Key, p.Key }) && f.Service.Current.Prs.Count == 0,
                "Single refresh enrichment failure or ignored draft blocked the move: " + string.Join(",", keys));
        });

        await test("completed history: detail or retained PoP re-check outside the current watch scope never enters history", async () =>
        {
            using var f = new Fixture();
            f.Store.Current.Whitelist.Add("Friend");
            var p = f.Add(); var friend = f.Add(id: 2); friend.Author = "Friend";
            f.CompletedResponse = ("""{"value":[]}""", HttpStatusCode.OK);
            await f.Service.RefreshAsync();
            var target = PopTarget.FromPr(f.View(p));
            f.Store.Current.Whitelist.Clear();
            friend.Status = "completed"; friend.Closed = "2026-10-31T00:00:00Z";
            await f.Service.RefreshOneAsync(friend.Key);
            f.Store.Current.Repos.Clear();
            p.Status = "completed"; p.Closed = "2026-10-31T00:00:00Z";
            var result = await f.Service.RefreshPopAsync(target);
            Check(result is not null && f.Service.Current.Completed.Count == 0, "Unwatched author or repository was reinserted into history");
        });

        await test("completed history: list completions use the watch scope current at publication, not refresh start", async () =>
        {
            foreach (var removeRepo in new[] { false, true })
            {
                using var f = new Fixture();
                f.Store.Current.Whitelist.Add("Friend");
                var listed = f.Add(); listed.Author = "Friend"; listed.Status = "completed"; listed.Closed = "2026-10-31T00:00:00Z";
                f.BeforeCompletedList = () => { if (removeRepo) f.Store.Current.Repos.Clear(); else f.Store.Current.Whitelist.Clear(); };
                await f.Service.RefreshAsync();
                Check(f.Service.Current.Completed.Count == 0, (removeRepo ? "Removed repository" : "Removed whitelisted author") + " still published list-derived history");
            }
        });

        await test("completed history: completions closed during a refresh are kept against the projection-time clock; later dates are not", async () =>
        {
            using var f = new Fixture();
            var a = f.Add(); var b = f.Add(id: 2);
            var listed = f.Add(id: 3); listed.Status = "completed"; listed.Closed = "2026-11-01T00:30:00Z";
            var future = f.Add(id: 4); future.Status = "completed"; future.Closed = "2026-11-01T05:00:00Z";
            f.BeforeCompletedList = () => f.Clock.Advance(TimeSpan.FromHours(1));
            f.BeforePrRead = (pr, _) =>
            {
                if (pr == a) { f.Clock.Advance(TimeSpan.FromHours(1)); a.Status = "completed"; a.Closed = "2026-11-01T01:30:00Z"; }
                if (pr == b) { b.Status = "completed"; b.Closed = "2026-11-01T05:00:00Z"; }
            };
            await f.Service.RefreshAsync();
            var s = f.Service.Current;
            var keys = s.Completed.Select(x => x.Key).ToList();
            Check(keys.Count(k => k == a.Key) == 1 && keys.Count(k => k == listed.Key) == 1 && !keys.Contains(future.Key) && !keys.Contains(b.Key),
                "Projection used a stale or missing upper bound: " + string.Join(",", keys));
            Check(s.Prs.All(x => x.Key != a.Key && x.Key != b.Key), "A terminal detail stayed active");
        });

        await test("completed history: wire cutoff is invariant under a non-Gregorian culture and future closed dates are rejected", async () =>
        {
            var culture = System.Globalization.CultureInfo.CurrentCulture;
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("th-TH");
            try
            {
                using var f = new Fixture();
                var listed = f.Add(); listed.Status = "completed"; listed.Closed = "2026-10-31T00:00:00Z";
                var future = f.Add(id: 2); future.Status = "completed"; future.Closed = "2026-11-02T00:00:00Z";
                var detail = f.Add(id: 3);
                await f.Service.RefreshAsync();
                Check(f.CompletedQueries.Single().Contains("searchCriteria.minTime=2026-10-25T00:00:00Z"), "Culture changed the wire cutoff: " + f.CompletedQueries.Single());
                Check(f.Service.Current.Completed.Single().Key == listed.Key, "Culture parse or future date changed history: " + string.Join(",", f.Service.Current.Completed.Select(x => x.Key)));
                detail.Status = "completed"; detail.Closed = "2026-11-03T00:00:00Z";
                await f.Service.RefreshOneAsync(detail.Key);
                Check(f.Service.Current.Completed.Single().Key == listed.Key, "Future-dated detail entered history");
            }
            finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
        });
    }

    sealed class Clock : TimeProvider
    {
        DateTimeOffset _now = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan span) => _now += span;
    }

    sealed class PullRequest(string repo, int id)
    {
        public string Repo = repo;
        public int Id = id;
        public string Key => $"{Repo}-id:{Id}";
        public string Status = "active";
        public string MergeStatus = "succeeded";
        public string? MergeFailure;
        public string Source = Fixture.Old;
        public string? Preview = Fixture.Old;
        public string Author = "me";
        public bool Draft;
        public string? Closed;
        public JsonNode Json() => new JsonObject
        {
            ["pullRequestId"] = Id, ["title"] = "Offline PR " + Id, ["status"] = Status, ["isDraft"] = Draft, ["closedDate"] = Closed,
            ["mergeStatus"] = MergeStatus, ["sourceRefName"] = "refs/heads/topic-" + Id, ["targetRefName"] = "refs/heads/main",
            ["mergeFailureMessage"] = MergeFailure,
            ["createdBy"] = new JsonObject { ["id"] = Author, ["displayName"] = Author },
            ["repository"] = new JsonObject { ["id"] = Repo + "-id", ["name"] = Repo,
                ["project"] = new JsonObject { ["id"] = "project-id" } },
            ["lastMergeTargetCommit"] = Preview is null ? null : new JsonObject { ["commitId"] = Preview },
            ["lastMergeSourceCommit"] = new JsonObject { ["commitId"] = Source },
            ["mergeOptions"] = Options.DeepClone(),
            ["autoCompleteSetBy"] = new JsonObject { ["displayName"] = "Offline" },
            ["creationDate"] = "2026-10-01T00:00:00Z",
        };
        static JsonNode Options => Fixture.Options;
    }

    sealed class Fixture : HttpMessageHandler, IDisposable
    {
        public static string Old => new('a', 40);
        public static string New => new('b', 40);
        public static JsonNode Options => JsonNode.Parse("""{"disableRenames":true,"conflictAuthorshipCommits":false,"detectRenameFalsePositives":true}""")!;
        public readonly Clock Clock = new();
        public readonly SettingsStore Store = new(new AppSettings { Repos = [], RefreshSeconds = 120, MergeCheckMinutes = 10 });
        public readonly PrService Service;
        public readonly Dictionary<string, string> Heads = new();
        public readonly HashSet<string> FailLists = [];
        public readonly HashSet<string> FailPrs = [];
        public readonly List<string> Patches = [];
        public readonly List<JsonNode> PatchBodies = [];
        public readonly JsonArray Policies = [];
        public HttpStatusCode? HeadFailure;
        public HttpStatusCode? PatchFailure;
        public string? PatchFailureKey;
        public bool ReturnQueued = true;
        public int CompletedNoise;
        public bool FailEnrichment;
        public (string Body, HttpStatusCode Status)? CompletedResponse;
        public readonly List<string> CompletedQueries = [];
        public Action<PullRequest, int>? BeforePrRead;
        public Action? BeforeCompletedList;
        public TaskCompletionSource? HoldPatch;
        public TaskCompletionSource PatchEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly List<PullRequest> _prs = [];
        readonly ConcurrentDictionary<string, int> _prReads = new();
        int _headReads;
        public int HeadReads => _headReads;
        readonly HttpClient _http;

        public Fixture()
        {
            _http = new(this, disposeHandler: false);
            var ado = new AdoClient(_http, _ => Task.FromResult(new AuthenticationHeaderValue("Bearer", "offline")));
            Service = new(ado, Store, Clock);
        }

        public PullRequest Add(string repo = "repo-one", int id = 1)
        {
            if (!Heads.ContainsKey(repo))
            {
                Heads[repo] = Old;
                Store.Current.Repos.Add(new RepoRef { Org = "offline", Project = "project", Repo = repo, RepoId = repo + "-id", ProjectId = "project-id" });
            }
            var pr = new PullRequest(repo, id); _prs.Add(pr); return pr;
        }
        public PrView View(PullRequest pr) => Service.Find(pr.Key);
        public void Complete(PullRequest pr) { pr.MergeStatus = "succeeded"; pr.Preview = Heads[pr.Repo]; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            Check(uri.Host == "dev.azure.com" && uri.AbsolutePath.StartsWith("/offline/"), "Fake handler received an unrelated origin");
            var path = uri.AbsolutePath;
            JsonNode result;
            if (path.EndsWith("/connectionData"))
                result = JsonNode.Parse("""{"authenticatedUser":{"id":"me","providerDisplayName":"Offline"}}""")!;
            else if (path.Contains("/policy/evaluations") && FailEnrichment)
                return Response("""{"message":"policy read denied"}""", HttpStatusCode.Forbidden);
            else if (path.Contains("/policy/evaluations"))
                result = new JsonObject { ["value"] = Policies.DeepClone() };
            else
            {
                var tail = path.Split("/repositories/")[1].Split('/');
                var repo = tail[0].EndsWith("-id") ? tail[0][..^3] : tail[0];
                Check(Heads.ContainsKey(repo), "Unknown repository");
                if (tail[1] == "refs")
                {
                    Interlocked.Increment(ref _headReads);
                    Check(Uri.UnescapeDataString(uri.Query).Contains("filter=heads/main"), "Wrong exact target ref read");
                    if (HeadFailure is { } failure)
                        return Response(failure == HttpStatusCode.OK ? """{"value":[]}""" : """{"message":"ref read denied"}""", failure);
                    result = new JsonObject { ["value"] = new JsonArray(new JsonObject { ["name"] = "refs/heads/main", ["objectId"] = Heads[repo] },
                        new JsonObject { ["name"] = "refs/heads/main-extra", ["objectId"] = New }) };
                }
                else if (tail.Length == 2 && uri.Query.Contains("searchCriteria.status=completed"))
                {
                    CompletedQueries.Add(Uri.UnescapeDataString(uri.Query));
                    BeforeCompletedList?.Invoke();
                    if (CompletedResponse is { } r) return Response(r.Body, r.Status);
                    // Unwatched completions fill the raw page so a cap can be reached with no watched match.
                    var noise = Enumerable.Range(9000, CompletedNoise).Select(n => { var x = new PullRequest(repo, n) { Status = "completed", Author = "stranger", Closed = "2026-10-31T00:00:00Z" }; return x.Json(); });
                    result = new JsonObject { ["value"] = new JsonArray(_prs.Where(p => p.Repo == repo && p.Status != "active").Select(p => p.Json()).Concat(noise).ToArray()) };
                }
                else if (tail.Length == 2)
                {
                    Check(uri.Query.Contains("searchCriteria.status=active"), "Not an active list");
                    if (FailLists.Contains(repo)) return Response("""{"message":"list denied"}""", HttpStatusCode.Forbidden);
                    result = new JsonObject { ["value"] = new JsonArray(_prs.Where(p => p.Repo == repo && p.Status == "active").Select(p => p.Json()).ToArray()) };
                }
                else
                {
                    var pr = _prs.Single(p => p.Repo == repo && p.Id == int.Parse(tail[2]));
                    if (request.Method == HttpMethod.Patch)
                    {
                        Check(tail.Length == 3, "Restart used the wrong endpoint");
                        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
                        Check(body.AsObject().Count == 1 && JsonNode.DeepEquals(body["mergeOptions"], Options), "Restart altered options, policy or completion state");
                        Patches.Add(pr.Key); PatchBodies.Add(body); PatchEntered.TrySetResult();
                        if (HoldPatch is not null) await HoldPatch.Task.WaitAsync(ct);
                        if (PatchFailure is { } failure && (PatchFailureKey is null || PatchFailureKey == pr.Key))
                            return Response("""{"message":"restart denied"}""", failure);
                        if (ReturnQueued) pr.MergeStatus = "queued";
                        result = pr.Json();
                    }
                    else if (tail.Length == 3)
                    {
                        BeforePrRead?.Invoke(pr, _prReads.AddOrUpdate(pr.Key, 1, (_, n) => n + 1));
                        if (FailPrs.Contains(pr.Key)) return Response("""{"message":"PR read denied"}""", HttpStatusCode.Forbidden);
                        result = pr.Json();
                    }
                    else if (tail[3] == "threads" || tail[3] == "iterations")
                        result = JsonNode.Parse("""{"value":[]}""")!;
                    else throw new Exception("Unexpected fake request: " + request.Method + " " + path);
                }
            }
            Check(request.Method == HttpMethod.Get || request.Method == HttpMethod.Patch, "Unexpected side effect");
            return Response(result.ToJsonString());
        }

        static HttpResponseMessage Response(string text, HttpStatusCode status = HttpStatusCode.OK) => new(status)
        {
            Content = new StringContent(text, Encoding.UTF8, "application/json"),
        };
        protected override void Dispose(bool disposing) { if (disposing) _http.Dispose(); base.Dispose(disposing); }
    }
}
