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

        await test("conflicts clear an accepted stale pending restart but a deliberate manual recheck remains available", async () =>
        {
            using var f = new Fixture();
            var p = f.Add(); p.Preview = Fixture.New; f.ReturnQueued = false;
            await f.Service.RefreshAsync();
            Check(f.View(p).MergeRecheckPending, "Fixture did not start a pending restart");
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
            await f.Service.RefreshAsync(); // Ready -> Needs Human, with unchanged target.
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

        await test("accepted but stale cached result stays pending until cadence, then retries instead of looping or waiting forever", async () =>
        {
            using var f = new Fixture();
            var p = f.Add(); p.Preview = Fixture.New;
            f.ReturnQueued = false;
            await f.Service.RefreshAsync();
            Check(f.View(p).MergeRecheckPending, "HTTP acceptance was treated as fresh computation");
            f.Clock.Advance(TimeSpan.FromMinutes(9)); await f.Service.RefreshAsync();
            Check(f.Patches.Count == 1 && f.View(p).MergeRecheckPending, "Cached terminal status caused duplicate restart");
            f.Clock.Advance(TimeSpan.FromMinutes(1)); await f.Service.RefreshAsync();
            Check(f.Patches.Count == 2, "Unchanged stale result never retried");
            f.Complete(p);
            await f.Service.RefreshAsync();
            Check(!f.View(p).MergeRecheckPending && f.View(p).Gates["merge"].Label == "Clean", "Recomputed terminal state not consumed");
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
        public string Source = Fixture.Old;
        public string? Preview = Fixture.Old;
        public string Author = "me";
        public bool Draft;
        public JsonNode Json() => new JsonObject
        {
            ["pullRequestId"] = Id, ["title"] = "Offline PR " + Id, ["status"] = Status, ["isDraft"] = Draft,
            ["mergeStatus"] = MergeStatus, ["sourceRefName"] = "refs/heads/topic-" + Id, ["targetRefName"] = "refs/heads/main",
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
        public HttpStatusCode? HeadFailure;
        public HttpStatusCode? PatchFailure;
        public string? PatchFailureKey;
        public bool ReturnQueued = true;
        public Action<PullRequest, int>? BeforePrRead;
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
            else if (path.Contains("/policy/evaluations"))
                result = JsonNode.Parse("""{"value":[]}""")!;
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
