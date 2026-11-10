using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AdoPrView;

static class ReviewerVoteTests
{
    const string UserId = "11111111-1111-1111-1111-111111111111";
    const string OtherId = "22222222-2222-2222-2222-222222222222";
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    static async Task Rejected(Fixture f, ReviewVoteTarget? target = null, bool ambiguous = false)
    {
        try { await f.Service.ApproveAsync("repo-id:123", target ?? f.Expected); }
        catch (InvalidOperationException ex)
        {
            if (ambiguous) Check(ex.Message.Contains("Approval not confirmed") && ex.Message.Contains("may have recorded"), "Write ambiguity was hidden");
            Check(f.Service.Find("repo-id:123").ReviewVote?.Vote != 10, "Unconfirmed result became success");
            return;
        }
        throw new Exception("Unsafe or unconfirmed approval accepted");
    }

    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("Reviewer vote bridge PUTs only authenticated identity and Approved 10; read-back preserves outstanding policies and neighbor", async () =>
        {
            using var f = new Fixture();
            await f.Init();
            string? reply = null;
            var bridge = new Bridge(f.Service, f.Store, text => { reply = text; return Task.CompletedTask; }, _ => { });
            await bridge.HandleAsync(JsonSerializer.Serialize(new { id = 4, method = "approve", @params = new { key = "repo-id:123", expected = f.Expected } }, Bridge.Json));
            var result = JsonNode.Parse(reply!)!["result"]!;
            Check(result["confirmed"]!.GetValue<bool>() && result["pr"]!["reviewVote"]!["vote"]!.GetValue<int>() == 10, "No confirmed own vote returned");
            Check(f.Puts == 1 && f.ReviewerReads == 1 && f.Service.Current.OrderRevision == 19, "Wrong write/read-back or order");
            Check(f.Service.Find("repo-id:123").HumanActions.Contains("reviews") && f.Service.Find("repo-id:123").Gates["reviews"].State == "human", "Own vote incorrectly cleared required reviews");
            Check(ReferenceEquals(f.Neighbor, f.Service.Find("repo-id:456")), "Unrelated PR changed");
        });
        await test("Reviewer vote never happens on refresh and captures ordinary negative vote before deliberate replacement", async () =>
        {
            using var f = new Fixture { Vote = -5 };
            await f.Init();
            Check(f.Puts == 0 && f.Expected.Vote == -5 && f.Service.Find("repo-id:123").ReviewVote!.CanApprove, "Refresh voted or captured wrong vote");
            var result = await f.Service.ApproveAsync("repo-id:123", f.Expected);
            Check(result.Confirmed && f.Puts == 1, "Deliberate negative vote replacement failed");
        });
        await test("Reviewer vote can add absent signed-in reviewer and does not synthesize another identity", async () =>
        {
            using var f = new Fixture { AbsentSelf = true };
            await f.Init();
            Check(f.Expected.Vote == 0, "Absent reviewer did not mean no vote");
            await Rejected(f, f.Expected with { ReviewerId = OtherId });
            Check(f.Puts == 0, "Client-selected reviewer reached PUT");
            var result = await f.Service.ApproveAsync("repo-id:123", f.Expected);
            Check(result.Confirmed && f.Puts == 1, "Could not add authenticated reviewer");
        });
        await test("Reviewer vote already recorded by ADO is read-only and does not imply minimum reviewers satisfied", async () =>
        {
            using var f = new Fixture();
            await f.Init();
            f.Vote = 10;
            var result = await f.Service.ApproveAsync("repo-id:123", f.Expected);
            Check(result.Confirmed && result.AlreadyApproved && f.Puts == 0 && !result.Pr.ReviewVote!.CanApprove, "Existing approval was written again");
            Check(result.Pr.HumanActions.Contains("reviews"), "Existing own vote incorrectly satisfied minimum reviewers");
        });
        foreach (var change in new[] { "account", "empty-account", "identity-container", "container", "missing-vote", "missing-reviewers", "changed-vote", "source", "target", "iteration", "source-ref", "target-ref", "pr-id", "repo-id", "project-id", "inactive", "pre-race", "pre-merge-source", "head-race" })
            await test("Reviewer vote preflight rejects " + change + " without a PUT", async () =>
            {
                using var f = new Fixture();
                await f.Init();
                f.Change = change;
                await Rejected(f);
                Check(f.Puts == 0, "Unsafe preflight reached vote API");
            });
        foreach (var change in new[] { "hidden", "unmonitored", "changed-repo", "unwatched" })
            await test("Reviewer vote rejects " + change + " settings without a PUT", async () =>
            {
                using var f = new Fixture();
                await f.Init();
                if (change == "hidden") f.Store.Current.Hidden.Add("repo-id:123");
                else if (change == "changed-repo") f.Store.Current.Repos[0].RepoId = "different-repository";
                else if (change == "unwatched") f.Store.Current.Whitelist.Clear();
                else f.Store.Current.Repos.Clear();
                await Rejected(f);
                Check(f.Puts == 0, "Ineligible PR reached vote API");
            });
        foreach (var failure in new[] { "put503", "put403", "put401", "put429", "put-network", "reviewer-read", "reviewer-mismatch", "reviewer-vote", "post-read", "post-source", "post-target", "post-iteration", "post-inactive", "post-head", "post-hidden" })
            await test("Reviewer vote " + failure + " is explicitly unconfirmed, with no automatic PUT retry", async () =>
            {
                using var f = new Fixture();
                await f.Init();
                f.Change = failure;
                await Rejected(f, ambiguous: true);
                Check(f.Puts == 1, "Vote was automatically retried");
            });
        await test("Reviewer vote native guard rejects a duplicate while PUT is in flight", async () =>
        {
            using var f = new Fixture();
            await f.Init();
            f.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = f.Service.ApproveAsync("repo-id:123", f.Expected);
            await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try { await f.Service.ApproveAsync("repo-id:123", f.Expected); throw new Exception("Duplicate accepted"); }
            catch (InvalidOperationException ex) { Check(ex.Message.Contains("pending"), "Wrong duplicate failure"); }
            f.Hold.SetResult();
            await first;
            Check(f.Puts == 1, "Duplicate reached transport");
        });
        await test("Reviewer vote shows self-approval policy credit separately from permission to record a vote", async () =>
        {
            using var f = new Fixture { IsAuthor = true };
            await f.Init();
            var before = f.Service.Find("repo-id:123").ReviewVote!;
            Check(before.CanApprove && before.Detail.Contains("author vote does not count"), "Author policy was confused with voting permission");
            var result = await f.Service.ApproveAsync("repo-id:123", f.Expected);
            Check(result.Pr.HumanActions.Contains("reviews") && result.Pr.ReviewVote!.Vote == 10, "Author vote was counted as all reviews");
        });
        await test("Reviewer vote supports monitored drafts without changing draft state", async () =>
        {
            using var f = new Fixture { Draft = true };
            await f.Init();
            var result = await f.Service.ApproveAsync("repo-id:123", f.Expected);
            Check(result.Confirmed && result.Pr.IsDraft && f.Puts == 1, "Monitored draft behavior changed");
        });
    }

    sealed class Fixture : IDisposable
    {
        public readonly SettingsStore Store = new(new AppSettings());
        public readonly PrView Neighbor = Seed(456);
        public readonly PrService Service;
        readonly HttpClient http;
        public int Vote, Puts, ReviewerReads, PrReads;
        public bool AbsentSelf, IsAuthor, Draft;
        public string Change = "";
        public ReviewVoteTarget Expected = null!;
        public TaskCompletionSource? Hold;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        string Source => Change is "source" || Change == "pre-race" && PrReads >= 2 || Change == "post-source" && Puts > 0 ? new('c', 40) : new('a', 40);
        string Target => Change == "target" || Change == "post-target" && Puts > 0 ? new('d', 40) : new('b', 40);

        static PrView Seed(int id) => new()
        {
            Key = "repo-id:" + id, Id = id, Title = "Offline PR", Org = "offline", Project = "project",
            Url = "https://dev.azure.com/offline/project/_git/repo/pullrequest/" + id,
            ProjectId = "project-id", Repo = "repo", RepoId = "repo-id", IsActive = true, Author = new("Author", null, OtherId),
        };
        public Fixture()
        {
            Store.Current.Repos.Add(new RepoRef { Org = "offline", Project = "project", ProjectId = "project-id", Repo = "repo", RepoId = "repo-id" });
            Store.Current.Whitelist.Add("Author");
            http = new HttpClient(new FakeHandler(Respond));
            Service = new PrService(new AdoClient(http, _ => Task.FromResult(new AuthenticationHeaderValue("Bearer", "offline"))), Store);
            typeof(PrService).GetField("_snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Service,
                new Snapshot { Prs = [Seed(123), Neighbor], OrderRevision = 19 });
        }
        public async Task Init()
        {
            await Service.RefreshOneAsync("repo-id:123");
            Expected = Service.Find("repo-id:123").ReviewVote?.Target ?? throw new Exception("Fixture did not capture review target");
            PrReads = 0;
        }
        JsonObject Reviewer() => new()
        {
            ["id"] = Change == "reviewer-mismatch" && Puts > 0 ? OtherId : UserId,
            ["displayName"] = "Offline Reviewer", ["vote"] = Change == "missing-vote" ? null : Change == "changed-vote" ? -10 : Change == "reviewer-vote" && Puts > 0 ? 0 : Vote,
            ["isContainer"] = Change == "container",
        };
        async Task<HttpResponseMessage> Respond(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Put)
            {
                Puts++;
                Check(path == $"/offline/project/_apis/git/repositories/repo-id/pullrequests/123/reviewers/{UserId}" &&
                    request.RequestUri.Query == "?api-version=7.1", "Wrong reviewer endpoint");
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
                Check(body.Count == 2 && body["id"]!.GetValue<string>() == UserId && body["vote"]!.GetValue<int>() == 10, "Vote payload added privileges or another reviewer");
                Entered.TrySetResult();
                if (Hold is not null) await Hold.Task;
                if (Change.StartsWith("put") && Change != "put-network")
                    return Json(new JsonObject(), Change switch { "put403" => HttpStatusCode.Forbidden, "put401" => HttpStatusCode.Unauthorized, "put429" => HttpStatusCode.TooManyRequests, _ => HttpStatusCode.ServiceUnavailable });
                Vote = 10;
                AbsentSelf = false;
                if (Change == "post-hidden") Store.Current.Hidden.Add("repo-id:123");
                if (Change == "put-network") throw new HttpRequestException("Offline response lost after write");
                return Json(Reviewer());
            }
            Check(request.Method == HttpMethod.Get, "Unexpected mutation");
            if (path.EndsWith("/connectionData"))
                return Json(new JsonObject { ["authenticatedUser"] = new JsonObject { ["id"] = Change == "account" ? OtherId : Change == "empty-account" ? "" : UserId,
                    ["providerDisplayName"] = "Offline Reviewer", ["isContainer"] = Change == "identity-container" } });
            if (path.EndsWith("/reviewers/" + UserId))
            {
                ReviewerReads++;
                return Json(Reviewer(), Change == "reviewer-read" ? HttpStatusCode.Forbidden : HttpStatusCode.OK);
            }
            if (path.EndsWith("/pullrequests/123"))
            {
                PrReads++;
                var pr = JsonNode.Parse("""{"pullRequestId":123,"status":"active","isDraft":false,"title":"Offline PR","sourceRefName":"refs/heads/fix","targetRefName":"refs/heads/main","mergeStatus":"succeeded","repository":{"id":"repo-id","name":"repo","project":{"id":"project-id"}}}""")!;
                pr["createdBy"] = new JsonObject { ["id"] = IsAuthor ? UserId : OtherId, ["displayName"] = "Author" };
                pr["lastMergeSourceCommit"] = new JsonObject { ["commitId"] = Change == "pre-merge-source" && PrReads >= 2 ? new string('f', 40) : Source };
                pr["lastMergeTargetCommit"] = new JsonObject { ["commitId"] = Target };
                pr["reviewers"] = AbsentSelf ? new JsonArray() : new JsonArray(Reviewer());
                if (Change == "missing-reviewers") pr.AsObject().Remove("reviewers");
                if (Change == "pr-id") pr["pullRequestId"] = 999;
                if (Change == "repo-id") pr["repository"]!["id"] = "other";
                if (Change == "project-id") pr["repository"]!["project"]!["id"] = "other";
                if (Change == "inactive" || Change == "post-inactive" && Puts > 0) pr["status"] = "completed";
                if (Change == "source-ref") pr["sourceRefName"] = "refs/heads/another";
                if (Change == "target-ref") pr["targetRefName"] = "refs/heads/another";
                pr["isDraft"] = Draft;
                return Json(pr, Change == "post-read" && Puts > 0 ? HttpStatusCode.Forbidden : HttpStatusCode.OK);
            }
            if (path.EndsWith("/iterations"))
                return Json(new JsonObject { ["value"] = new JsonArray(new JsonObject { ["id"] = Change == "iteration" || Change == "post-iteration" && Puts > 0 ? 5 : 4, ["sourceRefCommit"] = new JsonObject { ["commitId"] = Source } }) });
            if (path.EndsWith("/refs"))
            {
                var source = request.RequestUri.Query.Contains("fix");
                return Json(new JsonObject { ["value"] = new JsonArray(new JsonObject
                {
                    ["name"] = source ? "refs/heads/fix" : "refs/heads/main",
                    ["objectId"] = source ? Change == "head-race" || Change == "post-head" && Puts > 0 ? new string('e', 40) : Source : Target,
                }) });
            }
            if (path.EndsWith("/evaluations"))
                return Json(JsonNode.Parse("""{"value":[{"evaluationId":"review-policy","status":"rejected","configuration":{"isBlocking":true,"isEnabled":true,"type":{"id":"fa4e907d-c16b-4a4c-9dfa-4906e5d171dd","displayName":"Minimum number of reviewers"},"settings":{"creatorVoteCounts":false}}}]}""")!);
            if (path.EndsWith("/threads")) return Json(new JsonObject { ["value"] = new JsonArray() });
            throw new Exception("Unexpected offline GET " + path);
        }
        static HttpResponseMessage Json(JsonNode body, HttpStatusCode code = HttpStatusCode.OK) => new(code)
        { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        public void Dispose() => http.Dispose();
    }
}
