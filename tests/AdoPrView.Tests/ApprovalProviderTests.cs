using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json.Nodes;
using AdoPrView;

/// The only exported provider in the test assembly; the loader success path loads this assembly.
public sealed class TestApprovalProvider : IApprovalProvider
{
    public static int Version = ApprovalProviderContract.Version;
    public static Func<PrView, IApprovalHost, Task<ApprovalOutcome>>? OnRequest;
    public static readonly List<string> Requests = new();
    public int ContractVersion => Version;
    public string Name => "Test approvals";
    public string? LinkUrl => "https://approvals.example.test/";
    public string? LinkLabel => "Test site";
    public ApprovalOffer? Evaluate(PrView pr) => pr.Id == 123 ? new("Request test approval for only this PR", "Exact <b>detail</b>") : null;
    public async Task<ApprovalOutcome> RequestAsync(PrView pr, IApprovalHost host)
    {
        Requests.Add(pr.Key);
        return OnRequest is null ? new("ok", "Requested") : await OnRequest(pr, host);
    }
    public ApprovalSettingsView? GetSettings() => new("Configured", null, [new("reset", "Reset")]);
    public Task<string> InvokeSettingsActionAsync(string actionId, IApprovalUi ui) => Task.FromResult("Did " + actionId);
}

static class ApprovalProviderTests
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        var missing = Path.Combine(Path.GetTempPath(), "ado-provider-" + Guid.NewGuid().ToString("N"), "provider.dll");
        await test("approval provider is not loaded unless explicitly enabled, even when a component path is configured", () =>
        {
            foreach (var settings in new ApprovalProviderSettings?[] { null, new(), new() { AssemblyPath = missing },
                new() { AssemblyPath = typeof(TestApprovalProvider).Assembly.Location } })
            {
                var (provider, error) = ApprovalProviderLoader.Load(settings);
                Check(provider is null && error is null, "Disabled provider was loaded or reported");
            }
            return Task.CompletedTask;
        });
        await test("enabled approval provider without a usable component reports an explicit error", () =>
        {
            foreach (var path in new[] { null, "", "relative\\provider.dll", missing, typeof(Bridge).Assembly.Location })
            {
                var (provider, error) = ApprovalProviderLoader.Load(new() { Enabled = true, AssemblyPath = path });
                Check(provider is null && error?.StartsWith("The configured approval provider was not loaded: ") == true, "Missing provider was not explicit: " + path);
            }
            return Task.CompletedTask;
        });
        await test("enabled approval provider loads exactly one compatible exported provider", () =>
        {
            var settings = new ApprovalProviderSettings { Enabled = true, AssemblyPath = typeof(TestApprovalProvider).Assembly.Location };
            var (provider, error) = ApprovalProviderLoader.Load(settings);
            Check(provider is TestApprovalProvider && error is null, "Compatible provider did not load: " + error);
            TestApprovalProvider.Version = 99;
            try
            {
                (provider, error) = ApprovalProviderLoader.Load(settings);
                Check(provider is null && error!.Contains("contract version 99"), "Incompatible provider was accepted");
            }
            finally { TestApprovalProvider.Version = ApprovalProviderContract.Version; }
            return Task.CompletedTask;
        });
        await test("approval RPCs and requests are refused when no provider is enabled", async () =>
        {
            using var f = new Fixture(null);
            foreach (var call in new[] { """{"method":"generateApproval","params":{"key":"repo-id:123"}}""",
                """{"method":"approvalSettings"}""", """{"method":"approvalSettingsAction","params":{"id":"reset"}}""" })
                Check(await f.Rpc(call) == "No approval provider is enabled.", "Disabled provider RPC was dispatched: " + call);
            Check(f.Service.Current.ApprovalProvider is null && f.Service.Current.Prs.All(p => p.Approval is null), "Disabled provider exposed state");
            Check(f.Deletes.Count == 0, "Disabled provider made ADO writes");
        });
        await test("enabled approval provider acts only on an offered visible PR and its settings actions are allow-listed", async () =>
        {
            using var f = new Fixture(new TestApprovalProvider());
            TestApprovalProvider.Requests.Clear();
            Check(f.Service.Find("repo-id:123").Approval?.Title == "Request test approval for only this PR" && f.Service.Find("repo-id:456").Approval is null,
                "Offer was not evaluated per PR");
            Check(f.Service.Current.ApprovalProvider?.Name == "Test approvals", "Provider info missing from snapshot");
            Check(await f.Rpc("""{"method":"generateApproval","params":{"key":"repo-id:456"}}""") == "Test approvals is not available for this PR.", "Unoffered PR dispatched");
            Check(await f.Rpc("""{"method":"generateApproval","params":{"key":"repo-id:123"}}""") is null &&
                TestApprovalProvider.Requests.SequenceEqual(["repo-id:123"]), "Offered PR was not requested exactly once");
            Check(await f.Rpc("""{"method":"approvalSettingsAction","params":{"id":"other"}}""") == "Unknown approval settings action.", "Unlisted settings action dispatched");
            Check(await f.Rpc("""{"method":"approvalSettingsAction","params":{"id":"reset"}}""") is null, "Listed settings action failed");
        });
        await test("approval host removes a reviewer only from the captured PR with a GUID id", async () =>
        {
            using var f = new Fixture(new TestApprovalProvider());
            const string reviewer = "00000000-0000-4000-8000-0000000000c1";
            try
            {
                TestApprovalProvider.OnRequest = async (pr, host) =>
                {
                    foreach (var (target, id) in new[] { (f.Service.Find("repo-id:456"), reviewer), (pr, "not-a-guid") })
                    {
                        try { await host.RemoveReviewerAsync(target, id); throw new Exception("Out-of-scope reviewer change accepted"); }
                        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { }
                    }
                    await host.RemoveReviewerAsync(pr, reviewer);
                    return new("ok", "Removed");
                };
                await f.Service.RequestApprovalAsync("repo-id:123", f);
            }
            finally { TestApprovalProvider.OnRequest = null; }
            Check(f.Deletes.SequenceEqual([$"/contoso/Sample/_apis/git/repositories/repo-id/pullrequests/123/reviewers/{reviewer}"]), "Unexpected reviewer deletes: " + string.Join(", ", f.Deletes));
        });
    }

    sealed class Fixture : IApprovalUi, IDisposable
    {
        public readonly PrService Service;
        public readonly List<string> Deletes = new();
        readonly Bridge _bridge;
        readonly HttpClient _http;
        string? _reply;

        public Fixture(IApprovalProvider? provider)
        {
            _http = new HttpClient(new FakeHandler(request =>
            {
                Check(request.Method == HttpMethod.Delete, "Unexpected ADO request " + request.Method);
                Deletes.Add(request.RequestUri!.AbsolutePath);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }));
            var store = new SettingsStore(new AppSettings());
            Service = new PrService(new AdoClient(_http, _ => Task.FromResult(new AuthenticationHeaderValue("Bearer", "offline"))), store, provider);
            typeof(PrService).GetMethod("Publish", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Service, [new Snapshot { Prs = [Pr(123), Pr(456)] }]);
            _bridge = new Bridge(Service, store, text => { _reply = text; return Task.CompletedTask; }, _ => { }, provider, this);
        }

        static PrView Pr(int id) => new()
        {
            Key = "repo-id:" + id, Id = id, Title = "Sample PR", Org = "contoso", Project = "Sample", ProjectId = "project-id",
            Repo = "sample-repo", RepoId = "repo-id", IsActive = true, Author = new("Sample Author", null, null), Url = "https://dev.azure.com/contoso/Sample/_git/sample-repo/pullrequest/" + id,
        };

        public async Task<string?> Rpc(string request)
        {
            await _bridge.HandleAsync(request);
            return JsonNode.Parse(_reply!)!["error"]?.GetValue<string>();
        }

        public Task<T> OnUiThreadAsync<T>(Func<System.Windows.Window?, T> action) => Task.FromResult(action(null));
        public void Dispose() => _http.Dispose();
    }
}
