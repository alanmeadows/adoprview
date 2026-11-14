using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AdoPrView;

#pragma warning disable GHCP001

static class ExplainTests
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static async Task Throws<T>(Func<Task> run) where T : Exception
    {
        try { await run(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("Explain captures draft identity, immutable source/common-base and every change page using GET only", async () =>
        {
            using var f = new Fixture();
            using var context = await f.Capture();
            Check(f.Pr.IsDraft && context.Revision.SourceCommit == Fixture.Source && context.Revision.BaseCommit == Fixture.Base, "Wrong revision or draft excluded");
            Check(context.Changes.Count == 3 && context.Changes[2].Path == "/diagram.png", "Second page lost");
            Check(f.Urls.Any(u => u.Contains("$skip=2")) && f.Urls.Where(u => u.Contains("/changes?")).All(u => u.Contains("$compareTo=0")), "Wrong paging/comparison");
            Check(context.ListChanges(0).Contains("diagram.png"), "Wrong manifest");
        });
        await test("Explain diffs use common base instead of target and preserve exact before/after lines", async () =>
        {
            using var f = new Fixture();
            using var context = await f.Capture();
            var diff = await context.ReadChangeAsync(0, default);
            Check(diff.Contains("-        return TimeSpan.FromSeconds(2);") && diff.Contains("+        return TimeSpan.FromSeconds(Math.Min(300, 2 * Math.Pow(2, attempt)));"), "Diff omitted actual behavior");
            Check(f.Urls.Where(u => u.Contains("/items?")).All(u => u.Contains("versionDescriptor.versionType=commit") && (u.Contains(Fixture.Base) || u.Contains(Fixture.Source))), "Unpinned file read");
            Check(!f.Urls.Any(u => u.Contains(Fixture.Target)), "Target-only changes leaked into PR diff");
        });
        await test("Explain handles additions deletions renames binaries LFS and oversized files without inventing content", async () =>
        {
            using var f = new Fixture();
            using var context = await f.Capture();
            Check((await context.ReadChangeAsync(1, default)).Contains("+using Xunit;"), "Added file missing");
            Check((await context.ReadChangeAsync(2, default)).Contains("No complete text diff"), "Binary wasn't reported");
            Check(context.Coverage().Any(s => s.Contains("binary")), "Binary omission lost");
            f.Kind = "rename, edit"; f.OriginalPath = "/OldRetryPolicy.cs";
            using var rename = await f.Capture();
            Check((await rename.ReadChangeAsync(0, default)).Contains("--- /OldRetryPolicy.cs"), "Rename source path lost");
            f.Kind = "delete"; f.OriginalPath = null;
            using var deletion = await f.Capture();
            var start = f.Urls.Count;
            Check((await deletion.ReadChangeAsync(0, default)).Contains("-public"), "Deleted text not inspected");
            Check(f.Urls.Skip(start).Where(u => u.Contains("/items?")).All(u => u.Contains(Fixture.Base)), "Deletion fetched after content");
            f.Kind = "edit"; f.ContentOverride = new string('x', 80_001);
            using var large = await f.Capture();
            Check((await large.ReadChangeAsync(0, default)).Contains("No complete text diff") && large.Coverage().Any(s => s.Contains("80,000")), "Large file omission lost");
            f.ContentOverride = "version https://git-lfs.github.com/spec/v1\noid sha256:offline";
            using var lfs = await f.Capture();
            await lfs.ReadChangeAsync(0, default);
            Check(lfs.Coverage().Any(s => s.Contains("Git LFS")), "LFS pointer treated as payload");
        });
        await test("Explain rejects swapped PR/repository/project/fork identities before any file read", async () =>
        {
            using var f = new Fixture();
            foreach (var mismatch in new[] { "pr", "repo", "project", "fork" })
            {
                f.Mismatch = mismatch;
                await Throws<Exception>(async () => { using var unused = await f.Capture(); });
                Check(!f.Urls.Any(u => u.Contains("/items?")), "Unverified identity read files");
            }
        });
        await test("Explain refuses moved source/base/branch and nonadvancing pagination", async () =>
        {
            using var f = new Fixture();
            using var context = await f.Capture();
            f.MovedSource = true;
            await Throws<InvalidOperationException>(() => context.VerifyAsync(default));
            f.MovedSource = false; f.MovedBase = true;
            await Throws<InvalidOperationException>(() => context.VerifyAsync(default));
            f.MovedBase = false; f.Retargeted = true;
            await Throws<InvalidOperationException>(() => context.VerifyAsync(default));
            f.Retargeted = false; f.NewIteration = true;
            await Throws<InvalidOperationException>(() => context.VerifyAsync(default));
            f.NewIteration = false; f.BadPaging = true;
            await Throws<InvalidDataException>(async () => { using var unused = await f.Capture(); });
        });
        await test("Explain invalid paths/sides/ranges cannot change repository/revision or issue HTTP requests", async () =>
        {
            using var f = new Fixture();
            using var context = await f.Capture();
            var before = f.Urls.Count;
            foreach (var path in new[] { "https://evil.invalid/", "../x", "/../x", "/a/./b", "//host/x", "/a\\b", "/x?path=y", "/x#fragment" })
                await Throws<ArgumentException>(() => context.ReadFileAsync(path, "after", 1, default));
            await Throws<ArgumentException>(() => context.ReadFileAsync("/RetryPolicy.cs", Fixture.Target, 1, default));
            await Throws<ArgumentException>(() => context.ReadFileAsync("/RetryPolicy.cs", "after", 0, default));
            await Throws<ArgumentOutOfRangeException>(() => context.ReadChangeAsync(-1, default));
            Check(f.Urls.Count == before, "Invalid tool arguments reached HTTP");
            Check((await context.ReadFileAsync("/RetryPolicy.cs", "after", 2, default)).Contains("2: {"), "Line-numbered context missing");
        });
        await test("Explain discovers pinned repository context and records evidence beyond a one-line module diff", async () =>
        {
            using var f = new Fixture(dependencyOnly: true);
            using var context = await f.Capture();
            var diff = await context.ReadChangeAsync(0, default);
            Check(diff.Contains("-require golang.org/x/text v0.29.0") && diff.Contains("+require golang.org/x/text v0.30.0"), "Dependency fixture is not a one-line bump");
            Check((await context.ListDirectoryAsync("/", "after", default)).Contains("README.md"), "Cannot discover repository docs");
            Check(context.ContextReads == 0, "Directory names counted as inspected repository content");
            var readme = await context.ReadFileAsync("/README.md", "after", 1, default);
            Check(readme.Contains("developer utility") && readme.Contains("not compiled into"), "Component role missing from surrounding context");
            Check((await context.ListDirectoryAsync("/tools/fixturegen", "after", default)).Contains("main.go"), "Cannot discover module consumers");
            Check(context.ContextReads == 1 && context.Evidence().Any(s => s.Contains("README.md")), "Context reads not evidenced");
            var requests = f.Urls.Count;
            await Throws<ArgumentException>(() => context.ListDirectoryAsync("//evil.invalid", "after", default));
            await Throws<ArgumentException>(() => context.ListDirectoryAsync("/", "main", default));
            Check(f.Urls.Count == requests, "Invalid directory scope reached HTTP");
            f.WrongItem = true;
            await Throws<InvalidDataException>(() => context.ListDirectoryAsync("/", "after", default));
            f.WrongItem = false;
            await Throws<InvalidOperationException>(() => context.VerifyAsync(default));
        });
        await test("Explain context identity failures and ADO errors cannot become completed explanations", async () =>
        {
            using var f = new Fixture();
            using var context = await f.Capture();
            f.WrongItem = true;
            await Throws<InvalidDataException>(() => context.ReadChangeAsync(0, default));
            f.WrongItem = false;
            await Throws<InvalidOperationException>(() => context.VerifyAsync(default));
            using var denied = await f.Capture();
            f.DenyFiles = true;
            await Throws<AdoException>(() => denied.ReadChangeAsync(0, default));
            Check(denied.Coverage().Any(s => s.Contains("context read failed")), "Read error absent from coverage");
            await Throws<InvalidOperationException>(() => denied.VerifyAsync(default));
            f.DenyFiles = false;
            using var broken = await f.Capture();
            f.NetworkFailure = true;
            await Throws<HttpRequestException>(() => broken.ReadChangeAsync(0, default));
            f.NetworkFailure = false;
            await Throws<InvalidOperationException>(() => broken.VerifyAsync(default));
        });
        await test("Explain bounded HTTP reads reject oversized known and streaming response bodies", async () =>
        {
            foreach (var knownLength in new[] { true, false })
            {
                using var http = new HttpClient(new Handler(_ =>
                {
                    HttpContent content = knownLength ? new StringContent(new string('x', 2000)) : new UnknownLengthContent(new string('x', 2000));
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
                }));
                var ado = new AdoClient(http, _ => Task.FromResult(new AuthenticationHeaderValue("Bearer", "offline")));
                await Throws<InvalidDataException>(() => ado.GetBoundedJsonAsync("https://offline.invalid/", 1000, default));
            }
        });
        await test("Explain monitored/hidden checks permit drafts and reject removed repos before capture", async () =>
        {
            using var f = new Fixture();
            using var context = await f.Service.GetExplainContextAsync(f.Pr.Key, default);
            f.Store.Current.Hidden.Add(f.Pr.Key);
            await Throws<InvalidOperationException>(() => f.Service.GetExplainContextAsync(f.Pr.Key, default));
            f.Store.Current.Hidden.Clear(); f.Store.Current.Repos.Clear();
            await Throws<InvalidOperationException>(() => f.Service.GetExplainContextAsync(f.Pr.Key, default));
        });
        await test("Explain SDK policy explicitly disables ambient discovery/features and rejects permissions", async () =>
        {
            var config = ExplainModel.RestrictedSession("offline", []);
            Check(config.AvailableTools!.SequenceEqual(ExplainModel.ToolNames), "Unexpected allowlist");
            Check(config.EnableConfigDiscovery == false && config.SkipCustomInstructions == true &&
                config.EnableSkills == false && config.EnableFileHooks == false && config.EnableHostGitOperations == false &&
                config.EnableOnDemandInstructionDiscovery == false && config.EnableSessionStore == false &&
                config.InfiniteSessions?.Enabled == false && config.Memory?.Enabled == false &&
                config.ManageScheduleEnabled == false && config.CoauthorEnabled == false, "An ambient capability was enabled");
            var decision = await config.OnPermissionRequest!(new(), new());
            Check(!JsonSerializer.Serialize(decision).Contains("approve", StringComparison.OrdinalIgnoreCase), "Permission handler approved a tool");
            var environment = ExplainModel.ChildEnvironment();
            Check(environment.Keys.All(k => !k.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) && !k.Contains("PAT", StringComparison.OrdinalIgnoreCase) || k is "PATH" or "PATHEXT"), "Credentials inherited");
            Check(!environment.ContainsKey("NODE_OPTIONS") && !environment.ContainsKey("COPILOT_HOME"), "Ambient overrides inherited");
        });
        await test("Explain summary contract permits brief contextual prose but rejects reports and long output", () =>
        {
            Check(ExplainModel.IsBriefSummary("The developer utility that regenerates HTTP test fixtures upgrades x/text from v0.29.0 to v0.30.0. Its dependency changes; runtime behavior is not established."), "Short contextual summary rejected");
            Check(!ExplainModel.IsBriefSummary("One. Two. Three. Four."), "Four-sentence report accepted");
            Check(!ExplainModel.IsBriefSummary("one. two. three. four.") &&
                !ExplainModel.IsBriefSummary("`One.` Two. Three. Four."), "Sentence casing/formatting bypassed the limit");
            Check(!ExplainModel.IsBriefSummary(string.Join(" ", Enumerable.Repeat("word", 81))), "Word limit not enforced");
            Check(!ExplainModel.IsBriefSummary("## Summary\nDetails") && !ExplainModel.IsBriefSummary("- Update the module.") &&
                !ExplainModel.IsBriefSummary("A paragraph.\n\nAnother paragraph."), "Report formatting accepted");
            return Task.CompletedTask;
        });
        await test("Explain structured compression validates response shape and preserves the brief summary contract", async () =>
        {
            Check(ExplainModel.ReadStructuredSummary("""{"summary":"The fixture generator is a developer utility. Its dependency is updated."}""") ==
                "The fixture generator is a developer utility. Its dependency is updated.", "Structured summary content changed");
            await Throws<System.Text.Json.JsonException>(() => Task.FromResult(ExplainModel.ReadStructuredSummary("not JSON")));
            await Throws<InvalidDataException>(() => Task.FromResult(ExplainModel.ReadStructuredSummary("""{"summary":42}""")));
            await Throws<InvalidDataException>(() => Task.FromResult(ExplainModel.ReadStructuredSummary("""{"other":"A summary."}""")));
            await Throws<InvalidDataException>(() => Task.FromResult(ExplainModel.ReadStructuredSummary("""{"summary":"A summary.","extra":true}""")));
            await Throws<InvalidDataException>(() => Task.FromResult(ExplainModel.ReadStructuredSummary("""{"summary":"First.","summary":"Second."}""")));
            await Throws<InvalidDataException>(() => Task.FromResult(ExplainModel.ReadStructuredSummary(
                JsonSerializer.Serialize(new { summary = new string('x', 481) }))));
            await Throws<InvalidDataException>(() => Task.FromResult(ExplainModel.ReadStructuredSummary("""{"summary":"One. Two. Three. Four."}""")));
            Check(ExplainModel.SummarySchema.GetProperty("properties").GetProperty("summary").GetProperty("maxLength").GetInt32() == 480,
                "Structured response length is not bounded");
            await Throws<InvalidDataException>(() => Task.FromResult(ExplainModel.ReadStructuredSummary(
                System.Text.Json.JsonSerializer.Serialize(new { summary = string.Join(" ", Enumerable.Repeat("word", 81)) }))));
        });
        await test("Explain service deduplicates requests, fences cancellation IDs, streams and acknowledges cancellation", async () =>
        {
            using var f = new Fixture();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var fake = new Model(async (_, delta, ct) =>
            {
                delta("partial");
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return "unreachable";
            });
            await using var service = new ExplainService((_, ct) => f.Capture(ct), fake);
            var first = service.Start(f.Pr.Key);
            Check(first.RequestId == service.Start(f.Pr.Key).RequestId, "Duplicate model request");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(service.States.Single().Output == "partial", "No streaming text");
            await Throws<InvalidOperationException>(() => Task.FromResult(service.Start("other")));
            await Throws<InvalidOperationException>(() => service.CancelAsync(f.Pr.Key, "stale"));
            var cancelled = await service.CancelAsync(f.Pr.Key, first.RequestId);
            Check(cancelled.Status == "cancelled" && fake.Cancelled && cancelled.Output == "partial", "Cancellation not acknowledged/partial text lost");
            var again = service.Start(f.Pr.Key);
            Check(again.Sequence > cancelled.Sequence && again.RequestId != first.RequestId, "Restart did not fence old events");
            await service.CancelAsync(f.Pr.Key, again.RequestId);
        });
        await test("Explain service surfaces capture/model/revision errors and disposes pending work before its model", async () =>
        {
            var fake = new Model((_, _, _) => throw new InvalidOperationException("offline model unavailable"));
            await using (var service = new ExplainService((_, _) => throw new AdoException("offline ADO denied"), fake))
            {
                service.Start("offline");
                var failed = await Finish(service);
                Check(failed.Status == "failed" && failed.Message.Contains("ADO denied"), "Capture failure hidden");
            }
            using var f = new Fixture();
            await using (var service = new ExplainService((_, ct) => f.Capture(ct), new Model((_, _, _) => throw new InvalidOperationException("offline model unavailable"))))
            {
                service.Start(f.Pr.Key);
                Check((await Finish(service)).Message.Contains("model unavailable"), "Model failure hidden");
            }
            await using (var service = new ExplainService((_, ct) => f.Capture(ct), new Model((_, _, _) => { f.MovedSource = true; return Task.FromResult("unverified"); })))
            {
                service.Start(f.Pr.Key);
                Check((await Finish(service)).Status == "failed", "Revision drift became success");
            }
            f.MovedSource = false;
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = new Model(async (_, _, ct) => { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); return ""; });
            var disposing = new ExplainService((_, ct) => f.Capture(ct), pending);
            disposing.Start(f.Pr.Key);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await disposing.DisposeAsync();
            await disposing.DisposeAsync();
            Check(pending.Cancelled && pending.Disposed, "Shutdown left model work alive");
        });
        await test("Explain bridge routes separate captured key/request IDs and reports unavailable native host", async () =>
        {
            using var f = new Fixture();
            string? reply = null;
            var calls = new List<string>();
            var bridge = new Bridge(f.Service, f.Store, text => { reply = text; return Task.CompletedTask; }, _ => { },
                explainAction: (action, p) => { calls.Add(action + ":" + p["key"] + ":" + p["requestId"]); return Task.FromResult<object>(new { status = "offline" }); });
            await bridge.HandleAsync("""{"id":1,"method":"explainPr","params":{"key":"captured","url":"https://evil.invalid/"}}""");
            await bridge.HandleAsync("""{"id":2,"method":"cancelExplanation","params":{"key":"captured","requestId":"exact"}}""");
            Check(calls.SequenceEqual(["explainPr:captured:", "cancelExplanation:captured:exact"]), "Explain used Fix or lost identity");
            bridge = new Bridge(f.Service, f.Store, text => { reply = text; return Task.CompletedTask; }, _ => { });
            await bridge.HandleAsync("""{"id":3,"method":"explainPr","params":{"key":"captured"}}""");
            Check(reply!.Contains("unavailable") && reply.Contains("error"), "Unavailable host faked completion");
        });
    }

    static async Task<ExplainState> Finish(ExplainService service)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (service.States.Any(s => ExplainService.Active(s.Status)) && DateTime.UtcNow < until) await Task.Delay(10);
        var result = service.States.Single();
        Check(!ExplainService.Active(result.Status), "Explanation did not finish");
        return result;
    }

    sealed class Model(Func<ExplainContext, Action<string>, CancellationToken, Task<string>> run) : IExplainModel
    {
        public bool Cancelled, Disposed;
        public async Task<string> ExplainAsync(ExplainContext context, Action<string> delta, CancellationToken ct)
        {
            try { return await run(context, delta, ct); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    internal sealed class Fixture : IDisposable
    {
        public const string Source = "1111111111111111111111111111111111111111";
        public const string Base = "2222222222222222222222222222222222222222";
        public const string Target = "3333333333333333333333333333333333333333";
        public const string RepoId = "00000000-0000-4000-8000-000000000001";
        const string ProjectId = "00000000-0000-4000-8000-000000000002";
        public readonly PrView Pr;
        public readonly List<string> Urls = [];
        public readonly SettingsStore Store;
        public readonly PrService Service;
        readonly HttpClient _http;
        readonly AdoClient _ado;
        readonly bool _dependencyOnly;
        public string? Mismatch, OriginalPath, ContentOverride;
        public string Kind = "edit";
        public bool MovedSource, MovedBase, Retargeted, BadPaging, WrongItem, DenyFiles, NewIteration, NetworkFailure;
        public Fixture(bool dependencyOnly = false)
        {
            _dependencyOnly = dependencyOnly;
            Pr = new()
            {
                Key = RepoId + ":123", Id = 123, Title = dependencyOnly ? "Bump golang.org/x/text" : "Small internal cleanup",
                Url = $"https://dev.azure.com/offline/Sample/_git/{(dependencyOnly ? "harbor" : "retry")}/pullrequest/123",
                Org = "offline", Project = "Sample", ProjectId = ProjectId, Repo = dependencyOnly ? "harbor" : "retry", RepoId = RepoId,
                Author = new("Offline fixture", null, null), IsDraft = true, SourceBranch = "retry", TargetBranch = "main",
                Description = "Synthetic fixture. Establish the change and its role from code and repository context, not this title.",
            };
            Store = new(new AppSettings { Repos = [new() { Org = "offline", Project = "Sample", Repo = Pr.Repo, RepoId = RepoId, ProjectId = ProjectId }] });
            _http = new(new Handler(Respond));
            _ado = new(_http, _ => Task.FromResult(new AuthenticationHeaderValue("Bearer", "offline-fixture-not-a-credential")));
            Service = new(_ado, Store);
            PrService.Classify(Pr);
            typeof(PrService).GetField("_snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Service,
                new Snapshot { Prs = [Pr], LastRefresh = DateTimeOffset.UtcNow, Me = "Offline fixture" });
        }
        public Task<ExplainContext> Capture(CancellationToken ct = default) => ExplainContext.CaptureAsync(_ado, Pr, ct);
        HttpResponseMessage Respond(HttpRequestMessage request)
        {
            Check(request.Method == HttpMethod.Get, "Explanation attempted an ADO write");
            var uri = request.RequestUri!;
            Check(uri.Host == "dev.azure.com" && uri.AbsolutePath.StartsWith($"/offline/{ProjectId}/_apis/git/repositories/{RepoId}/", StringComparison.Ordinal), "Explanation escaped repository scope");
            var url = uri.ToString(); Urls.Add(url);
            object result;
            var source = MovedSource ? Target : Source;
            var iteration = new { id = NewIteration ? 5 : 4, sourceRefCommit = new { commitId = source }, commonRefCommit = new { commitId = MovedBase ? Target : Base }, targetRefCommit = new { commitId = Target } };
            if (uri.AbsolutePath.EndsWith("/pullrequests/123"))
                result = new { pullRequestId = Mismatch == "pr" ? 999 : 123, title = Pr.Title, status = "active", isDraft = true,
                    sourceRefName = "refs/heads/retry", targetRefName = Retargeted ? "refs/heads/other" : "refs/heads/main",
                    repository = new { id = Mismatch == "repo" ? ProjectId : RepoId, project = new { id = Mismatch == "project" ? RepoId : ProjectId } },
                    forkSource = Mismatch == "fork" ? new { repository = new { id = ProjectId } } : null,
                    lastMergeSourceCommit = new { commitId = source } };
            else if (uri.AbsolutePath.EndsWith("/iterations")) result = new { value = new[] { iteration } };
            else if (uri.AbsolutePath.EndsWith("/iterations/4")) result = iteration;
            else if (uri.AbsolutePath.EndsWith("/changes"))
            {
                var second = url.Contains("$skip=2");
                var entries = new object[]
                {
                    new { item = new { path = "/RetryPolicy.cs" }, changeType = Kind, originalPath = OriginalPath },
                    new { item = new { path = "/RetryPolicyTests.cs" }, changeType = "add" },
                    new { item = new { path = "/diagram.png" }, changeType = "add" },
                };
                result = _dependencyOnly
                    ? (object)new { changeEntries = new[] { new { item = new { path = "/tools/fixturegen/go.mod" }, changeType = "edit" } }, nextSkip = 0, nextTop = 200 }
                    : new { changeEntries = second ? entries[2..] : entries[..2], nextSkip = second && !BadPaging ? 0 : 2, nextTop = 200 };
            }
            else if (uri.AbsolutePath.EndsWith("/items"))
            {
                if (NetworkFailure) throw new HttpRequestException("offline connection interrupted");
                if (DenyFiles) return new(HttpStatusCode.Forbidden) { Content = new StringContent("""{"message":"offline file access denied"}""") };
                var query = uri.Query.TrimStart('?').Split('&').Select(s => s.Split('=', 2)).ToDictionary(p => p[0], p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : "");
                var commit = query["versionDescriptor.version"];
                if (query.TryGetValue("scopePath", out var scope))
                {
                    var paths = _dependencyOnly
                        ? new[] { "/README.md", "/tools", "/tools/fixturegen", "/tools/fixturegen/README.md", "/tools/fixturegen/go.mod", "/tools/fixturegen/main.go" }
                        : new[] { "/README.md", "/RetryPolicy.cs", "/RetryPolicyTests.cs", "/diagram.png" };
                    var prefix = scope.TrimEnd('/') + "/";
                    var children = paths.Where(p => p.StartsWith(prefix, StringComparison.Ordinal) && !p[prefix.Length..].Contains('/'));
                    result = new { value = children.Select(p => new { path = p, commitId = WrongItem ? Target : commit,
                        isFolder = p is "/tools" or "/tools/fixturegen" }).ToArray() };
                    return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(result), Encoding.UTF8, "application/json") };
                }
                var path = query["path"];
                string? content = path switch
                {
                    "/README.md" => _dependencyOnly
                        ? "# Harbor\nHarbor is the customer-facing gateway service. tools/fixturegen is a developer utility that regenerates HTTP integration-test fixtures. It is not compiled into the gateway."
                        : "# Retry\nAn internal integration-test harness. RetryPolicy controls retries made by that harness, not a shipped product.",
                    "/tools/fixturegen/README.md" => "# Fixturegen\nA manually run developer utility that regenerates JSON examples for integration tests when contracts change. x/text is used to normalize fixture names.",
                    "/tools/fixturegen/go.mod" => "module example.invalid/harbor/tools/fixturegen\n\ngo 1.24\n\nrequire golang.org/x/text " + (commit == Base ? "v0.29.0" : "v0.30.0") + "\n",
                    "/tools/fixturegen/main.go" => """
                        package main
                        import ("encoding/json"; "os"; "golang.org/x/text/cases")
                        func main() { json.NewEncoder(os.Stdout).Encode(map[string]string{"name": cases.Fold().String(os.Args[1])}) }
                        """,
                    "/RetryPolicyTests.cs" => """
                    using Xunit;

                    public class RetryPolicyTests
                    {
                        [Theory]
                        [InlineData(0, 2)]
                        [InlineData(1, 4)]
                        [InlineData(20, 300)]
                        public void DelayDoublesUntilCapped(int attempt, double expectedSeconds)
                        {
                            Assert.Equal(expectedSeconds, RetryPolicy.Delay(attempt).TotalSeconds);
                        }
                    }
                    """,
                    "/RetryPolicy.cs" or "/OldRetryPolicy.cs" or "/diagram.png" => "public static class RetryPolicy\n{\n    public static TimeSpan Delay(int attempt)\n    {\n" +
                        (commit == Base ? "        return TimeSpan.FromSeconds(2);" : "        return TimeSpan.FromSeconds(Math.Min(300, 2 * Math.Pow(2, attempt)));") + "\n    }\n}\n",
                    _ => null,
                };
                if (content is null) return new(HttpStatusCode.NotFound) { Content = new StringContent("""{"message":"fixture path not found"}""") };
                result = new { path = WrongItem ? "/Unrelated.cs" : path, commitId = commit, gitObjectType = "blob",
                    contentMetadata = new { isBinary = path.EndsWith(".png") },
                    content = query.ContainsKey("includeContent") ? ContentOverride ?? content : null };
            }
            else throw new Exception("Unexpected offline ADO endpoint: " + url);
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(result), Encoding.UTF8, "application/json") };
        }
        public void Dispose() => _http.Dispose();
    }

    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
    sealed class UnknownLengthContent(string text) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(Encoding.UTF8.GetBytes(text)).AsTask();
    }
}
