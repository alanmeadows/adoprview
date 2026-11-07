using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AdoPrView;

static class ReviewBotScopeTests
{
    const string BotId = "00000000-0000-4000-8000-0000000000e1";
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("review bot gate applies only to its organization within one two-organization refresh", async () =>
        {
            var bot = new ReviewBotSettings { Name = "Team bot", AuthorId = BotId, Organization = "Contoso" };
            var (prs, errors) = await Refresh(bot);
            var scoped = prs.Single(p => p.Org == "contoso");
            var other = prs.Single(p => p.Org == "fabrikam");
            Check(scoped.Agency?.State == "partial" && scoped.Gates["agency"].State == "warn" && scoped.Waiting.Count == 0,
                "Configured organization lost its partial-coverage semantics");
            Check(other.Agency?.State == "na" && other.Gates["agency"].State == "na" && other.Waiting.Count == 0 &&
                other.Threads.Count == 1 && !other.Threads[0].IsAgency, "Unrelated organization got a review-bot gate or bot ownership");
            Check(errors.Count == 0, "Scoped bot reported an error");
        });
        await test("review bot without an organization is reported and applied nowhere, config unchanged", async () =>
        {
            var bot = new ReviewBotSettings { Name = "Team bot", AuthorId = BotId, Organization = " " };
            var (prs, errors) = await Refresh(bot);
            Check(prs.All(p => p.Agency?.State == "na" && p.Gates["agency"].State == "na"), "Unscoped bot was applied");
            Check(errors.Count == 1 && errors[0].Contains("reviewBot.organization"), "Unscoped bot was not reported");
            Check(bot is { Name: "Team bot", AuthorId: BotId, Organization: " " }, "Customized bot config was changed");
        });
        foreach (var bot in new ReviewBotSettings?[] { null, new() { Name = "Unused", AuthorId = " ", Organization = "contoso" } })
            await test("unconfigured review bot manufactures no gate or error: " + (bot is null ? "absent" : "empty id"), async () =>
            {
                var (prs, errors) = await Refresh(bot);
                Check(prs.Count == 2 && prs.All(p => p.Agency?.State == "na" && p.Waiting.Count == 0) && errors.Count == 0, "Unconfigured bot changed state");
            });
    }

    static async Task<(List<PrView> Prs, List<string> Errors)> Refresh(ReviewBotSettings? bot)
    {
        var store = new SettingsStore(new AppSettings
        {
            ReviewBot = bot,
            Repos = new[] { "contoso", "fabrikam" }.Select(o => new RepoRef { Org = o, Project = "project", Repo = "repo", RepoId = o + "-repo", ProjectId = "project-id" }).ToList(),
        });
        using var http = new HttpClient(new Handler());
        var service = new PrService(new AdoClient(http, _ => Task.FromResult(new AuthenticationHeaderValue("Bearer", "offline"))), store);
        await service.RefreshAsync();
        Check(service.Current.Error is null, "Refresh failed: " + service.Current.Error);
        return (service.Current.Prs.ToList(), service.Current.RepoErrors.ToList());
    }

    sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            Check(request.Method == HttpMethod.Get && uri.Host == "dev.azure.com", "Unexpected request " + request.Method + " " + uri);
            var org = uri.AbsolutePath.Split('/')[1];
            var path = uri.AbsolutePath;
            var main = new string('a', 40);
            // The bot posted a partial-coverage report in contoso; fabrikam only has a human thread.
            var thread = org == "contoso"
                ? $$"""{"id":5,"status":"closed","comments":[{"author":{"id":"{{BotId}}","displayName":"Team bot"},"content":"Iteration #1 **Partial review**","commentType":"text"}]}"""
                : """{"id":6,"status":"active","comments":[{"author":{"id":"human","displayName":"Riley Reviewer"},"content":"Question","commentType":"text"}]}""";
            var pr = $$$"""
                {"pullRequestId":1,"status":"active","title":"Sample {{{org}}}","createdBy":{"id":"me","displayName":"Me"},
                 "repository":{"id":"{{{org}}}-repo","name":"repo","project":{"id":"project-id"}},"sourceRefName":"refs/heads/fix","targetRefName":"refs/heads/main",
                 "mergeStatus":"succeeded","lastMergeSourceCommit":{"commitId":"source"},"lastMergeTargetCommit":{"commitId":"{{{main}}}"},"creationDate":"2026-01-01T00:00:00Z"}
                """;
            var json =
                path.EndsWith("/connectionData") ? """{"authenticatedUser":{"id":"me","providerDisplayName":"Me"}}""" :
                path.EndsWith("/pullrequests") ? $$"""{"value":[{{pr}}]}""" :
                path.EndsWith("/pullrequests/1") ? pr :
                path.EndsWith("/threads") ? $$"""{"value":[{{thread}}]}""" :
                path.EndsWith("/iterations") ? """{"value":[{"id":1,"sourceRefCommit":{"commitId":"source"},"updatedDate":"2026-01-01T00:00:00Z"}]}""" :
                path.EndsWith("/refs") ? $$"""{"value":[{"name":"refs/heads/main","objectId":"{{main}}"}]}""" :
                path.EndsWith("/evaluations") ? """{"value":[]}""" :
                throw new Exception("Unexpected read " + path);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
