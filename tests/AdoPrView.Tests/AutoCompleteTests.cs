using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using AdoPrView;

static class AutoCompleteTests
{
    const string UserId = "11111111-1111-1111-1111-111111111111";
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("Auto-complete RPC returns captured GET-confirmed PR and refreshes only that authoritative state", async () =>
        {
            using var f = new Fixture();
            string? reply = null;
            var bridge = new Bridge(f.Service, f.Store, text => { reply = text; return Task.CompletedTask; }, _ => { });
            await bridge.HandleAsync("""{"id":42,"method":"autoComplete","params":{"key":"repo-id:123","enable":true}}""");
            var result = JsonNode.Parse(reply!)!["result"]!;
            Check(result["key"]!.GetValue<string>() == "repo-id:123" && result["autoCompleteBy"]!.GetValue<string>() == "Offline Reviewer", "RPC lacks exact confirmed PR");
            Check(result["gates"]!["autocomplete"]!["state"]!.GetValue<string>() == "ok", "Gate did not consume actual GET");
            Check(f.Patches == 1 && f.Reads == 1 && f.Service.Current.OrderRevision == 19, "Unexpected writes/read-back/order revision");
            Check(ReferenceEquals(f.Neighbor, f.Service.Find("repo-id:456")) && f.Neighbor.AutoCompleteBy is null, "Neighbor changed");
        });
        await test("Auto-complete cancellation confirms null identity and restores actionable human gate", async () =>
        {
            using var f = new Fixture { Set = true };
            await f.Service.SetAutoCompleteAsync("repo-id:123", true);
            var result = await f.Service.SetAutoCompleteAsync("repo-id:123", false);
            Check(result.AutoCompleteBy is null && result.HumanActions.Contains("autocomplete"), "Cancellation did not restore actionable state");
            Check(result.Gates["autocomplete"].State == "human" && f.Patches == 2, "Wrong cancelled gate or duplicate request");
        });
        foreach (var failure in new[] { "patch", "get", "mismatch", "identity" })
            await test("Auto-complete " + failure + " failure is not confirmed success and writes are never retried", async () =>
            {
                using var f = new Fixture { Failure = failure };
                try { await f.Service.SetAutoCompleteAsync("repo-id:123", true); throw new Exception("Unconfirmed action returned success"); }
                catch (Exception ex) when (ex is AdoException or InvalidOperationException) { }
                Check(f.Patches == 1 && f.Service.Find("repo-id:123").AutoCompleteBy is null, "Failure became success or PATCH retried");
            });
        await test("Auto-complete missing display name uses confirmed identity; empty identity cannot claim enabled", async () =>
        {
            using var f = new Fixture { DisplayName = "" };
            var result = await f.Service.SetAutoCompleteAsync("repo-id:123", true);
            Check(result.AutoCompleteBy == UserId, "Missing display name hid confirmed auto-complete");
            f.Failure = "empty-id";
            try { await f.Service.SetAutoCompleteAsync("repo-id:123", true); throw new Exception("Empty identity confirmed"); }
            catch (InvalidOperationException) { }
        });
        await test("Auto-complete native guard blocks duplicate set or cancel while exact PR request is pending", async () =>
        {
            using var f = new Fixture { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            var first = f.Service.SetAutoCompleteAsync("repo-id:123", true);
            await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            foreach (var enabled in new[] { true, false })
            {
                try { await f.Service.SetAutoCompleteAsync("repo-id:123", enabled); throw new Exception("Duplicate accepted"); }
                catch (InvalidOperationException ex) { Check(ex.Message.Contains("pending"), "Wrong guard failure"); }
            }
            f.Hold.SetResult();
            await first;
            Check(f.Patches == 1, "Duplicate reached transport");
        });
    }

    sealed class Fixture : IDisposable
    {
        public readonly SettingsStore Store = new(new AppSettings());
        public readonly PrView Neighbor = Pr(456);
        public readonly PrService Service;
        readonly HttpClient http;
        public bool Set;
        public string Failure = "", DisplayName = "Offline Reviewer";
        public int Patches, Reads;
        public TaskCompletionSource? Hold;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        static PrView Pr(int id) => new()
        {
            Key = "repo-id:" + id, Id = id, Title = "Offline PR", Url = "https://dev.azure.com/offline/project/_git/repo/pullrequest/" + id,
            Org = "offline", Project = "project",
            ProjectId = "project-id", Repo = "repo", RepoId = "repo-id", IsActive = true, Author = new("Offline", null, UserId),
        };

        public Fixture()
        {
            http = new HttpClient(new FakeHandler(async request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (request.Method == HttpMethod.Patch)
                {
                    Patches++;
                    Check(path.EndsWith("/pullrequests/123"), "Wrong PR was modified");
                    var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
                    var enable = body["autoCompleteSetBy"]!["id"]!.GetValue<string>() != Guid.Empty.ToString();
                    if (enable)
                    {
                        Check(body["completionOptions"]!["mergeStrategy"]!.GetValue<string>() == "squash" &&
                            body["completionOptions"]!["deleteSourceBranch"]!.GetValue<bool>() &&
                            body["completionOptions"]!["transitionWorkItems"]!.GetValue<bool>(), "Existing merge options changed");
                    }
                    Entered.TrySetResult();
                    if (Hold is not null) await Hold.Task;
                    if (Failure == "patch") return Json("{}", HttpStatusCode.ServiceUnavailable);
                    if (Failure != "mismatch") Set = enable;
                    return Json("{}");
                }
                Check(request.Method == HttpMethod.Get, "Unexpected mutation");
                if (path.EndsWith("/connectionData")) return Json(new JsonObject
                { ["authenticatedUser"] = new JsonObject { ["id"] = UserId, ["providerDisplayName"] = "Offline Reviewer" } }.ToJsonString());
                if (path.EndsWith("/pullrequests/123"))
                {
                    Reads++;
                    if (Failure == "get") return Json("{}", HttpStatusCode.Forbidden);
                    var pr = JsonNode.Parse("""{"pullRequestId":123,"status":"active","title":"Offline PR","sourceRefName":"refs/heads/fix","targetRefName":"refs/heads/main","mergeStatus":"succeeded","repository":{"id":"repo-id","name":"repo","project":{"id":"project-id"}}}""")!;
                    if (Failure == "identity") pr["pullRequestId"] = 999;
                    if (Set) pr["autoCompleteSetBy"] = new JsonObject { ["id"] = Failure == "empty-id" ? "" : UserId, ["displayName"] = DisplayName };
                    return Json(pr.ToJsonString());
                }
                if (path.EndsWith("/refs")) return Json("""{"value":[{"name":"refs/heads/main","objectId":"1111111111111111111111111111111111111111"}]}""");
                if (path.EndsWith("/evaluations") || path.EndsWith("/threads") || path.EndsWith("/iterations")) return Json("""{"value":[]}""");
                throw new Exception("Unexpected offline GET " + path);
            }));
            Service = new PrService(new AdoClient(http, _ => Task.FromResult(new AuthenticationHeaderValue("Bearer", "offline"))), Store);
            typeof(PrService).GetField("_snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Service,
                new Snapshot { Prs = [Pr(123), Neighbor], OrderRevision = 19 });
        }

        static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) => new(code)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        public void Dispose() => http.Dispose();
    }
}
