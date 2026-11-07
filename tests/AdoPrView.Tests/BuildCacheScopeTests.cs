using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AdoPrView;

static class BuildCacheScopeTests
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    // Same build 123 / log 7 in every scope, with different diagnostics and log lengths.
    static readonly (string Org, string Project, int Lines)[] Scopes = [("alpha", "one", 500), ("beta", "one", 40), ("alpha", "two", 120)];

    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("failed build diagnostics, log tails and Fix prompt never cross organizations or projects with equal build ids", async () =>
        {
            var handler = new Handler();
            var store = new SettingsStore(new AppSettings { Repos = [] });
            using var http = new HttpClient(handler);
            var service = new PrService(new AdoClient(http, _ => Task.FromResult(new AuthenticationHeaderValue("Bearer", "offline"))), store);
            foreach (var (org, project, _) in Scopes)
            {
                // Fetch scopes one after another so a cache keyed only by build id would be hit.
                store.Current.Repos.Add(new RepoRef { Org = org, Project = project, Repo = "repo", RepoId = $"{org}-{project}-repo", ProjectId = project + "-id" });
                await service.RefreshAsync();
                Check(service.Current.Error is null && service.Current.RepoErrors.Count == 0, "Refresh failed");
            }
            foreach (var (org, project, lines) in Scopes)
            {
                var tag = $"{org}-{project}";
                var pr = service.Current.Prs.Single(p => p.Org == org && p.Project == project);
                var fb = pr.FailedBuilds.Single();
                Check(fb.BuildId == 123 && fb.EvaluationId == "ev-" + tag && fb.Url.Contains($"/{org}/{project}/_build/") &&
                    fb.Tasks.Single().Name == "Task " + tag && fb.Tasks[0].Issues.SequenceEqual(["Issue " + tag]), "Failed build diagnostics came from another scope: " + tag);
                var tail = await service.GetLogTailAsync(pr.Key, 123, 7, 60);
                Check(tail == $"log {tag} lines {Math.Max(1, lines - 59)}-{lines}", $"Log tail for {tag} was wrong: {tail}");
                tail = await service.GetLogTailAsync(pr.Key, 123, 7);
                Check(tail == $"log {tag} lines {Math.Max(1, lines - 199)}-{lines}", $"Default log tail for {tag} was wrong: {tail}");
                var prompt = await service.BuildTriagePromptAsync(pr.Key);
                Check(prompt.Contains("Task " + tag) && prompt.Contains("Issue " + tag) && prompt.Contains($"/{org}/{project}/_build/results?buildId=123") &&
                    prompt.Contains($"log {tag} lines"), "Fix prompt lacks its own build data: " + tag);
                foreach (var (otherOrg, otherProject, _) in Scopes.Where(s => (s.Org, s.Project) != (org, project)))
                {
                    var other = $"{otherOrg}-{otherProject}";
                    Check(!prompt.Contains("Task " + other) && !prompt.Contains("Issue " + other) && !prompt.Contains("log " + other) &&
                        !prompt.Contains($"/{otherOrg}/{otherProject}/_build/"), $"Fix prompt for {tag} contains {other} data");
                }
            }
            foreach (var (org, project, _) in Scopes)
                Check(handler.Reads[$"{org}/{project}/timeline"] == 1 && handler.Reads[$"{org}/{project}/logs"] == 1,
                    "Completed build data was not shared within its own organization/project");
        });
    }

    sealed class Handler : HttpMessageHandler
    {
        public readonly ConcurrentDictionary<string, int> Reads = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            Check(request.Method == HttpMethod.Get && uri.Host == "dev.azure.com", "Unexpected request " + request.Method + " " + uri);
            var seg = uri.AbsolutePath.Split('/');
            var org = seg[1];
            var path = uri.AbsolutePath;
            if (path.EndsWith("/connectionData"))
                return Json("""{"authenticatedUser":{"id":"me","providerDisplayName":"Me"}}""");
            var project = seg[2];
            var tag = $"{org}-{project}";
            var lines = Scopes.Single(s => s.Org == org && s.Project == project).Lines;
            var main = new string('a', 40);
            var pr = $$$"""
                {"pullRequestId":1,"status":"active","title":"Sample","createdBy":{"id":"me","displayName":"Me"},
                 "repository":{"id":"{{{tag}}}-repo","name":"repo","project":{"id":"{{{project}}}-id"}},"sourceRefName":"refs/heads/fix","targetRefName":"refs/heads/main",
                 "mergeStatus":"succeeded","lastMergeSourceCommit":{"commitId":"source"},"lastMergeTargetCommit":{"commitId":"{{{main}}}"},"creationDate":"2026-01-01T00:00:00Z"}
                """;
            if (path.EndsWith("/timeline")) Reads.AddOrUpdate($"{org}/{project}/timeline", 1, (_, n) => n + 1);
            if (path.EndsWith("/builds/123/logs")) Reads.AddOrUpdate($"{org}/{project}/logs", 1, (_, n) => n + 1);
            if (path.EndsWith("/builds/123/logs/7"))
            {
                var q = System.Web.HttpUtility.ParseQueryString(uri.Query);
                return Text($"log {tag} lines {q["startLine"]}-{q["endLine"]}");
            }
            return Json(
                path.EndsWith("/pullrequests") ? $$"""{"value":[{{pr}}]}""" :
                path.EndsWith("/pullrequests/1") ? pr :
                path.EndsWith("/threads") ? """{"value":[]}""" :
                path.EndsWith("/iterations") ? """{"value":[{"id":1,"sourceRefCommit":{"commitId":"source"},"updatedDate":"2026-01-01T00:00:00Z"}]}""" :
                path.EndsWith("/refs") ? $$"""{"value":[{"name":"refs/heads/main","objectId":"{{main}}"}]}""" :
                path.EndsWith("/evaluations") ? $$$"""
                    {"value":[{"evaluationId":"ev-{{{tag}}}","status":"rejected","context":{"buildId":123},
                      "configuration":{"isEnabled":true,"isBlocking":true,"type":{"id":"0609b952-1397-4640-95ec-e00a01b2c241","displayName":"Build"},"settings":{"displayName":"CI"} } }]}
                    """ :
                path.EndsWith("/timeline") ? $$$"""
                    {"records":[{"id":"s","type":"Stage","name":"Stage","state":"completed","result":"failed"},
                      {"id":"t","parentId":"s","type":"Task","name":"Task {{{tag}}}","result":"failed","finishTime":"2026-01-01T00:00:00Z","log":{"id":7},
                       "issues":[{"type":"error","message":"Issue {{{tag}}}"}]}]}
                    """ :
                path.EndsWith("/builds/123/logs") ? """{"value":[{"id":7,"lineCount":""" + lines + "}]}" :
                throw new Exception("Unexpected read " + path));
        }

        static Task<HttpResponseMessage> Json(string json) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        static Task<HttpResponseMessage> Text(string text) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/plain") });
    }
}
