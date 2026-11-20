using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AdoPrView;

static class InvestigationTests
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static async Task Throws<T>(Func<Task> run) where T : Exception
    {
        try { await run(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    static async Task<InvestigationState> Finish(InvestigationService service)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (service.States.Any(s => InvestigationService.Active(s.Status)) && DateTime.UtcNow < until) await Task.Delay(10);
        var result = service.States.Single();
        Check(!InvestigationService.Active(result.Status), "Investigation did not finish");
        return result;
    }

    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("Investigation retains follow-up history, isolates PR identities and refuses stale conversation IDs", async () =>
        {
            using var f = new ExplainTests.Fixture();
            var source = new Source(await f.Capture());
            var histories = new List<InvestigationTurn[]>();
            var captures = 0;
            await using var service = new InvestigationService((_, _) => { captures++; return Task.FromResult<IInvestigationSource>(source); },
                (_, _, _, _, _, _, _, _) => throw new Exception("Question attempted a write"),
                new Model((_, history, _, question, _) =>
                {
                    histories.Add(history);
                    return Task.FromResult(new InvestigationAnswer("Answer: " + question, []));
                }));
            var first = service.Ask(f.Pr.Key, "", "question", "What changes?");
            var done = await Finish(service);
            service.Ask(f.Pr.Key, first.ConversationId, "question", "Why does that help?");
            done = await Finish(service);
            Check(done.Turns.Length == 2 && histories[0].Length == 0 && histories[1][0].Question == "What changes?" && captures == 1, "Follow-up lost its pinned context/history");
            await Throws<InvalidOperationException>(() => Task.FromResult(service.Ask(f.Pr.Key, "stale", "question", "Wrong")));
            await Throws<InvalidOperationException>(() => Task.FromResult(service.Ask("other-pr", first.ConversationId, "question", "Wrong PR")));
            f.MovedSource = true;
            service.Ask(f.Pr.Key, first.ConversationId, "question", "Still current?");
            done = await Finish(service);
            Check(done.Status == "failed" && done.Message.Contains("revisions changed") && done.Turns.Length == 2, "Stale PR accepted a new answer");
        });
        foreach (var postOutcomes in new[] { false, true })
        await test($"Investigation context limit counts serialized findings and posting outcomes (posted={postOutcomes})", async () =>
        {
            using var f = new ExplainTests.Fixture();
            var source = new Source(await f.Capture());
            var requests = 0;
            var findings = Enumerable.Range(0, 8).Select(i => new LensFinding(i.ToString(), new string('t', 200),
                new string('b', 4000), "/RetryPolicy.cs", 1, new string('q', 2000))).ToArray();
            await using var service = new InvestigationService((_, _) => Task.FromResult<IInvestigationSource>(source),
                (_, _, _, _, _, selected, _, _) => Task.FromResult(selected.Select(finding =>
                    new CommentOutcome(finding.Id, "failed", new string('m', 5000), null)).ToArray()),
                new Model((_, _, _, _, _) =>
                {
                    requests++;
                    return Task.FromResult(new InvestigationAnswer(new string('a', 16000), findings));
                }));
            var start = service.Ask(f.Pr.Key, "", "lens", "Synthetic bounded lens");
            var done = await Finish(service);
            if (postOutcomes)
                done = await service.PostAsync(f.Pr.Key, start.ConversationId, start.RequestId, findings.Select(x => x.Id).ToArray(), true);
            else
            {
                service.Ask(f.Pr.Key, start.ConversationId, "lens", "Synthetic follow-up lens");
                done = await Finish(service);
            }
            Check(done.Turns.Sum(turn => turn.Answer.Length + turn.Question.Length) < 100000 &&
                JsonSerializer.Serialize(done.Turns, Bridge.Json).Length > 100000, "Fixture did not exceed the serialized history budget");
            var before = requests;
            await Throws<InvalidOperationException>(() => Task.FromResult(service.Ask(f.Pr.Key, start.ConversationId, "question", "Too much history")));
            Check(requests == before, "Oversized history reached the model");
        });
        await test("Investigation cancellation fences turn IDs, keeps prior turns, and source setup failures are explicit", async () =>
        {
            using var f = new ExplainTests.Fixture();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var source = new Source(await f.Capture());
            await using (var service = new InvestigationService((_, _) => Task.FromResult<IInvestigationSource>(source),
                (_, _, _, _, _, _, _, _) => throw new Exception("Unexpected write"), new Model(async (_, _, _, _, ct) =>
                { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); return new("unreachable", []); })))
            {
                var start = service.Ask(f.Pr.Key, "", "question", "Wait?");
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Throws<InvalidOperationException>(() => service.CancelAsync(f.Pr.Key, start.ConversationId, "stale"));
                await Throws<InvalidOperationException>(() => Task.FromResult(service.Ask("other", "", "question", "Parallel?")));
                var cancelled = await service.CancelAsync(f.Pr.Key, start.ConversationId, start.RequestId);
                Check(cancelled.Status == "cancelled" && cancelled.Turns.Length == 0, "Cancellation accepted partial output");
            }
            Check(source.Disposed, "Shutdown did not dispose the owned source");
            await using var broken = new InvestigationService((_, _) => throw new InvalidOperationException("No matching WSL remote"),
                (_, _, _, _, _, _, _, _) => throw new Exception("Unexpected write"), new Model((_, _, _, _, _) => throw new Exception("Model should not start")));
            broken.Ask(f.Pr.Key, "", "question", "No source?");
            var failure = await Finish(broken);
            Check(failure.Status == "failed" && failure.Message.Contains("No matching WSL remote") && failure.Worktree is null, "Setup failure claimed worktree access");
        });
        await test("Investigation reset releases only its owned source and recaptures fresh conversation identity", async () =>
        {
            using var f = new ExplainTests.Fixture();
            var sources = new List<Source>();
            await using var service = new InvestigationService(async (_, ct) =>
            {
                var source = new Source(await f.Capture(ct));
                sources.Add(source); return source;
            }, (_, _, _, _, _, _, _, _) => throw new Exception("Unexpected write"),
                new Model((_, _, _, _, _) => Task.FromResult(new InvestigationAnswer("A grounded answer.", []))));
            var start = service.Ask(f.Pr.Key, "", "question", "First");
            await Finish(service);
            var reset = await service.ResetAsync(f.Pr.Key, start.ConversationId);
            Check(reset.ConversationId != start.ConversationId && reset.Turns.Length == 0 && sources[0].Disposed, "Reset kept old identity or source");
            service.Ask(f.Pr.Key, reset.ConversationId, "question", "Fresh");
            await Finish(service);
            Check(sources.Count == 2, "New conversation did not recapture source");
            await service.DisposeAsync();
            await service.DisposeAsync();
        });
        await test("Investigation lens generation is read-only and posting requires selected current preview and explicit confirmation", async () =>
        {
            using var f = new ExplainTests.Fixture();
            var calls = 0;
            var finding = new LensFinding("0", "A meaningful compatibility failure", "Concrete triggering behavior.", "/RetryPolicy.cs", 1, "public");
            var source = new Source(await f.Capture());
            await using var service = new InvestigationService((_, _) => Task.FromResult<IInvestigationSource>(source),
                (_, _, _, _, lens, selected, _, _) =>
                {
                    calls++;
                    Check(lens == "Does this work with the caller?", "Posted comment lost the concrete review lens");
                    Check(selected.Length == 1 && selected[0] == finding, "Unselected findings posted");
                    return Task.FromResult(new[] { new CommentOutcome("0", "posted", "confirmed", "https://dev.azure.com/offline/thread") });
                }, new Model((_, _, _, _, _) => Task.FromResult(new InvestigationAnswer("Scoped preview.", [finding, finding with { Id = "1" }]))));
            var start = service.Ask(f.Pr.Key, "", "lens", "Does this work with the caller?");
            var done = await Finish(service);
            Check(calls == 0 && done.Turns[0].Findings.Length == 2, "Lens generation wrote to ADO");
            await Throws<ArgumentException>(() => service.PostAsync(f.Pr.Key, start.ConversationId, start.RequestId, ["0"], false));
            await Throws<ArgumentException>(() => service.PostAsync(f.Pr.Key, start.ConversationId, start.RequestId, [], true));
            await Throws<InvalidOperationException>(() => service.PostAsync(f.Pr.Key, start.ConversationId, "stale", ["0"], true));
            done = await service.PostAsync(f.Pr.Key, start.ConversationId, start.RequestId, ["0"], true);
            Check(calls == 1 && done.Turns[0].Outcomes.Length == 1 && done.Message.Contains("1 comment(s)") && done.Message.Contains("1 not selected"), "Posting outcome was overstated");
            f.MovedBase = true;
            done = await service.PostAsync(f.Pr.Key, start.ConversationId, start.RequestId, ["1"], true);
            Check(done.Status == "failed" && calls == 1, "Revision drift reached write delegate");
        });
        await test("Investigation validates lens response shapes, exact inspected evidence and empty findings", async () =>
        {
            using var f = new ExplainTests.Fixture();
            var context = await f.Capture();
            var transport = new Transport();
            await using var source = await InvestigationSource.CreateAsync(context, f.Pr, f.Store.Current, default, transport);
            string Response(string quote = "line two", int line = 2, string path = "/RetryPolicy.cs", string title = "Trigger",
                string body = "Meaningful consequence.", int count = 1) => JsonSerializer.Serialize(new
            { answer = "A scoped finding.", findings = Enumerable.Repeat(new { title, body, path, line, quote }, count) });
            await Throws<System.IO.InvalidDataException>(() => Task.FromResult(InvestigationModel.Parse(Response(), "lens", source)));
            await source.ReadFileAsync("/RetryPolicy.cs", "after", 1, default);
            Check(InvestigationModel.Parse(Response(), "lens", source).Findings.Single().Line == 2, "Exact observed evidence rejected");
            foreach (var invalid in new[] { Response("invented"), Response(line: 0), Response(line: 9), Response(path: "/Unchanged.cs"),
                Response(title: new string('x', 201)), Response(body: new string('x', 4001)), Response(count: 9),
                JsonSerializer.Serialize(new { answer = new string('x', 32001), findings = Array.Empty<object>() }) })
                await Throws<System.IO.InvalidDataException>(() => Task.FromResult(InvestigationModel.Parse(invalid, "lens", source)));
            await Throws<System.IO.InvalidDataException>(() => Task.FromResult(InvestigationModel.Parse(Response(), "question", source)));
            var none = InvestigationModel.Parse("""{"answer":"No findings established within the inspected scope.","findings":[]}""", "lens", source);
            Check(none.Findings.Length == 0, "No findings manufactured");
            var question = InvestigationModel.ParseQuestion("  The `case \"conflicts\"` branch moves classification to **Blocked**.\n\nThis does not merge anything.  ");
            Check(question.Answer.EndsWith("anything.") && question.Answer.Contains("\"conflicts\"") && question.Findings.Length == 0,
                "Ordinary Markdown question lost code/text or gained postable findings");
            Check(InvestigationModel.ParseQuestion("""{"answer":"Quoted data","findings":[{"title":"not a postable finding"}]}""").Findings.Length == 0,
                "Question text was interpreted as postable findings");
            foreach (var invalid in new[] { "", " \n ", new string('x', 32001) })
                await Throws<System.IO.InvalidDataException>(() => Task.FromResult(InvestigationModel.ParseQuestion(invalid)));
            var before = transport.Calls.Count;
            await Throws<ArgumentException>(() => source.ReadFileAsync("/../secret", "after", 1, default));
            await Throws<ArgumentException>(() => source.ReadFileAsync("/RetryPolicy.cs", "main", 1, default));
            Check(transport.Calls.Count == before, "Invalid tool arguments reached WSL");
        });
        await test("Investigation accepts omitted diff attempts only with actual worktree content and retains omission notices", async () =>
        {
            foreach (var (index, content, notice) in new[]
            {
                (0, "version https://git-lfs.github.com/spec/v1\noid sha256:offline", "Git LFS"),
                (0, new string('x', 80_001), "80,000"),
                (2, "", "binary"),
            })
            {
                using var f = new ExplainTests.Fixture { ContentOverride = content };
                await using var source = await InvestigationSource.CreateAsync(await f.Capture(), f.Pr, f.Store.Current, default, new Transport());
                await Throws<InvalidOperationException>(() => Task.FromResult(Inspect()));
                await source.Context.ReadChangeAsync(index, default);
                Check(source.Context.DiffReads == 1 && !source.Context.Evidence().Any(e => e.StartsWith("Diff: ")),
                    "Omitted diff was reported as inspected content");
                await Throws<InvalidOperationException>(() => Task.FromResult(Inspect()));
                await source.ReadFileAsync("/RetryPolicy.cs", "after", 1, default);
                Inspect();
                Check(source.Context.Coverage().Any(e => e.Contains(notice)),
                    "Accepted limited investigation lost the diff omission");
                await source.VerifyAsync(default);
                bool Inspect() { InvestigationModel.RequireInspection(source); return true; }
            }
        });
        await test("Investigation permits an empty captured change inventory only after actual worktree content is read", async () =>
        {
            using var f = new ExplainTests.Fixture();
            f.AdditionalResponse = request => request.RequestUri!.AbsolutePath.EndsWith("/changes")
                ? Json(new { changeEntries = Array.Empty<object>(), nextSkip = 0 }) : null;
            await using var source = await InvestigationSource.CreateAsync(await f.Capture(), f.Pr, f.Store.Current, default, new Transport());
            Check(source.Context.Changes.Count == 0, "Fixture did not capture an empty inventory");
            await Throws<InvalidOperationException>(() => Task.FromResult(Inspect()));
            await source.ReadFileAsync("/RetryPolicy.cs", "after", 1, default);
            Inspect();
            await Throws<InvalidDataException>(() =>
            {
                source.ValidateFinding(new("0", "Failure", "Consequence", "/RetryPolicy.cs", 2, "line two"));
                return Task.CompletedTask;
            });
            bool Inspect() { InvestigationModel.RequireInspection(source); return true; }
        });
        await test("Investigation source rejects an unrelated configured checkout before worktree creation", async () =>
        {
            using var f = new ExplainTests.Fixture();
            using var context = await f.Capture();
            var transport = new Transport { WrongRemote = true };
            await Throws<InvalidOperationException>(() => InvestigationSource.CreateAsync(context, f.Pr, f.Store.Current, default, transport));
            Check(transport.Calls.Count == 1, "Unverified repository reached worktree creation");
        });
        await test("Investigation posting is idempotent and reports partial and ambiguous writes without POST retries", async () =>
        {
            using var f = new ExplainTests.Fixture();
            using var context = await f.Capture();
            var threads = new List<JsonNode>();
            var posts = 0;
            var failSecond = true;
            f.AdditionalResponse = request =>
            {
                if (!request.RequestUri!.AbsolutePath.EndsWith("/threads")) return null;
                if (request.Method == HttpMethod.Get) return Json(new { value = threads });
                Check(request.Method == HttpMethod.Post, "Wrong comment verb");
                posts++;
                if (posts == 2 && failSecond) throw new HttpRequestException("offline ambiguous response");
                var thread = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!;
                thread["id"] = posts + 20;
                thread["comments"]![0]!["id"] = 1;
                threads.Add(thread);
                Check(thread["threadContext"]!["rightFileStart"]!["line"]!.GetValue<int>() == 2, "Comment lost line evidence");
                return Json(thread);
            };
            var findings = new[] { new LensFinding("0", "First", "Concrete first failure", "/RetryPolicy.cs", 2, "{"),
                new LensFinding("1", "Second", "Concrete second failure", "/RetryPolicy.cs", 2, "{") };
            var result = await f.Service.PostLensFindingsAsync(f.Pr.Key, context.Revision, "conversation", "turn", "Compatibility with the caller", findings, [], default);
            Check(posts == 2 && result[0].Status == "posted" && result[0].Url!.Contains("discussionId=21") && result[1].Status == "uncertain", "Partial posting not reported truthfully");
            failSecond = false;
            result = await f.Service.PostLensFindingsAsync(f.Pr.Key, context.Revision, "conversation", "turn", "Compatibility with the caller", findings, result, default);
            Check(posts == 2 && result[1].Status == "uncertain", "Ambiguous POST retried automatically");
            var reconciled = await f.Service.PostLensFindingsAsync(f.Pr.Key, context.Revision, "conversation", "turn", "Compatibility with the caller", findings[..1], [], default);
            Check(posts == 2 && reconciled[0].Status == "posted", "Marker reconciliation posted duplicate");
            f.MovedSource = true;
            await Throws<InvalidOperationException>(() => f.Service.PostLensFindingsAsync(f.Pr.Key, context.Revision, "conversation", "turn2", "Compatibility with the caller", findings, [], default));
            Check(posts == 2, "Stale preview posted comments");
        });
        await test("Investigation ADO denied comment remains failed with no posted link and is never retried", async () =>
        {
            using var f = new ExplainTests.Fixture();
            using var context = await f.Capture();
            var posts = 0;
            f.AdditionalResponse = request =>
            {
                if (!request.RequestUri!.AbsolutePath.EndsWith("/threads")) return null;
                if (request.Method == HttpMethod.Get) return Json(new { value = Array.Empty<object>() });
                posts++;
                return new(HttpStatusCode.Forbidden) { Content = new StringContent("""{"message":"Synthetic permission denied"}""") };
            };
            var result = await f.Service.PostLensFindingsAsync(f.Pr.Key, context.Revision, "conversation", "turn", "Compatibility",
                [new("0", "Failure", "Trigger and consequence", "/RetryPolicy.cs", 2, "{")], [], default);
            Check(posts == 1 && result.Single().Status == "failed" && result[0].Url is null, "Denied comment became success or retried");
        });
        foreach (var invalidResponse in new[] { "{bad JSON", "null" })
        await test($"Investigation reconciles a committed comment after invalid POST JSON ({invalidResponse}) without losing earlier outcomes", async () =>
        {
            using var f = new ExplainTests.Fixture();
            using var context = await f.Capture();
            var posts = 0;
            var threads = new List<JsonNode>();
            f.AdditionalResponse = request =>
            {
                if (!request.RequestUri!.AbsolutePath.EndsWith("/threads")) return null;
                if (request.Method == HttpMethod.Get) return Json(new { value = threads });
                posts++;
                var thread = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!;
                thread["id"] = posts + 50; thread["comments"]![0]!["id"] = 1; threads.Add(thread);
                return posts == 1 ? Json(thread) : new(HttpStatusCode.OK) { Content = new StringContent(invalidResponse) };
            };
            var result = await f.Service.PostLensFindingsAsync(f.Pr.Key, context.Revision, "conversation", "turn", "Compatibility",
                [new("0", "First", "Concrete first failure", "/RetryPolicy.cs", 2, "{"),
                 new("1", "Second", "Concrete second failure", "/RetryPolicy.cs", 2, "{")], [], default);
            Check(posts == 2 && result.Length == 2 && result.All(o => o.Status == "posted" && o.Url is not null), "Malformed response lost confirmed comments or retried POST");
        });
        foreach (var drift in new[] { false, true })
        await test($"Investigation uncertain replay stays uncertain after failed read-back or revision check (drift={drift})", async () =>
        {
            using var f = new ExplainTests.Fixture();
            using var context = await f.Capture();
            var posts = 0;
            var reads = 0;
            var blocked = true;
            f.AdditionalResponse = request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/pullrequests/123") && blocked && drift && ++reads == 2)
                    f.MovedSource = true;
                if (!request.RequestUri.AbsolutePath.EndsWith("/threads")) return null;
                if (request.Method == HttpMethod.Post) { posts++; throw new HttpRequestException("Unexpected replay"); }
                if (blocked && !drift) throw new HttpRequestException("Synthetic read-back unavailable");
                return Json(new { value = Array.Empty<object>() });
            };
            var findings = new[] { new LensFinding("0", "Failure", "Trigger", "/RetryPolicy.cs", 2, "{") };
            var prior = new[] { new CommentOutcome("0", "uncertain", "Earlier response lost.", null) };
            var result = await f.Service.PostLensFindingsAsync(f.Pr.Key, context.Revision, "conversation", "turn", "Compatibility", findings, prior, default);
            Check(result.Single().Status == "uncertain" && posts == 0, "Uncertain write became definitely unposted");
            blocked = false; f.MovedSource = false;
            result = await f.Service.PostLensFindingsAsync(f.Pr.Key, context.Revision, "conversation", "turn", "Compatibility", findings, result, default);
            Check(result.Single().Status == "uncertain" && posts == 0, "Blocked reconciliation removed the no-replay guard");
        });
        await test("Investigation confirmed text cannot publish hidden Markdown targets or HTML and freshness reads no diff inventory", async () =>
        {
            using var f = new ExplainTests.Fixture();
            using var context = await f.Capture();
            var start = f.Urls.Count;
            var posts = 0;
            f.AdditionalResponse = request =>
            {
                if (!request.RequestUri!.AbsolutePath.EndsWith("/threads")) return null;
                if (request.Method == HttpMethod.Get) return Json(new { value = Array.Empty<object>() });
                posts++;
                var thread = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!;
                var content = thread["comments"]![0]!["content"]!.GetValue<string>();
                Check(content.Contains(@"\!\[hidden\]\(https://example\.invalid/image\)") &&
                    content.Contains(@"\[label\]\(https://example\.invalid/target\)") && content.Contains("&lt;img"),
                    "Comment published Markdown/HTML absent from literal preview");
                thread["id"] = posts + 60; thread["comments"]![0]!["id"] = 1;
                return Json(thread);
            };
            var result = await f.Service.PostLensFindingsAsync(f.Pr.Key, context.Revision, "conversation", "turn",
                "[label](https://example.invalid/target)", Enumerable.Range(0, 8).Select(i => new LensFinding(i.ToString(), "Failure",
                    "![hidden](https://example.invalid/image) <img src='https://example.invalid/html'>", "/RetryPolicy.cs", 2, "{")).ToArray(), [], default);
            Check(posts == 8 && result.All(o => o.Status == "posted"), "Confirmed batch did not finish");
            var requests = f.Urls.Skip(start).ToArray();
            Check(requests.Length == 68 && requests.All(u => !u.Contains("/changes?")), "Freshness recaptured the change inventory");
        });
        foreach (var sourceMoves in new[] { false, true })
        await test($"Investigation final write check rejects live branch drift despite unchanged merge previews (source={sourceMoves})", async () =>
        {
            using var f = new ExplainTests.Fixture { TargetPreview = ExplainTests.Fixture.Target };
            using var context = await f.Capture();
            var posts = 0;
            var reads = 0;
            f.AdditionalResponse = request =>
            {
                if (!request.RequestUri!.AbsolutePath.EndsWith("/threads")) return null;
                if (request.Method == HttpMethod.Post) { posts++; throw new Exception("Stale branch reached POST"); }
                if (++reads == 1)
                {
                    if (sourceMoves) f.SourceHead = ExplainTests.Fixture.Base;
                    else f.TargetHead = ExplainTests.Fixture.Base;
                }
                return Json(new { value = Array.Empty<object>() });
            };
            var result = await f.Service.PostLensFindingsAsync(f.Pr.Key, context.Revision, "conversation", "turn", "Compatibility",
                [new("0", "Failure", "Concrete consequence", "/RetryPolicy.cs", 2, "{")], [], default);
            Check(result.Single().Status == "failed" && posts == 0, "Stale source or target branch was posted");
        });
        await test("Investigation initializes the validated environment exactly once per source operation", async () =>
        {
            using var f = new ExplainTests.Fixture();
            var transport = new Transport();
            var source = await InvestigationSource.CreateAsync(await f.Capture(), f.Pr, f.Store.Current, default, transport);
            await source.ReadFileAsync("/RetryPolicy.cs", "after", 1, default);
            await source.ListDirectoryAsync("/", "after", default);
            await source.VerifyAsync(default);
            await source.DisposeAsync();
            Check(transport.Calls.Count >= 9 && transport.Calls.All(call =>
                call.Script.StartsWith(CopilotRunService.SourceEnvironmentScript + "\n", StringComparison.Ordinal) &&
                call.Script.IndexOf(CopilotRunService.SourceEnvironmentScript, StringComparison.Ordinal) ==
                call.Script.LastIndexOf(CopilotRunService.SourceEnvironmentScript, StringComparison.Ordinal) &&
                !call.Script.Contains("nvm use", StringComparison.Ordinal)),
                "Fresh source shells lost the authenticated environment or initialized it twice");
        });
        if (Environment.GetEnvironmentVariable("ADOPRVIEW_TEST_WSL_INVESTIGATION") == "1")
            await test("Investigation synthetic Git worktree readers never run filters or follow symlinks and preserve primary checkout", RunGitFixture);
    }

    static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    sealed class Model(Func<IInvestigationSource, InvestigationTurn[], string, string, CancellationToken, Task<InvestigationAnswer>> run) : IInvestigationModel
    {
        public Task<InvestigationAnswer> AskAsync(IInvestigationSource source, InvestigationTurn[] history, string kind, string question, CancellationToken ct) => run(source, history, kind, question, ct);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class Source(ExplainContext context) : IInvestigationSource
    {
        public bool Disposed;
        public ExplainContext Context => context;
        public string Location => "offline isolated worktree";
        public string[] Evidence => [];
        public Task<string> ReadFileAsync(string path, string side, int startLine, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> ListDirectoryAsync(string path, string side, CancellationToken ct) => throw new NotSupportedException();
        public void ValidateFinding(LensFinding finding) { }
        public Task VerifyAsync(CancellationToken ct) => context.VerifyAsync(ct);
        public ValueTask DisposeAsync() { Disposed = true; context.Dispose(); return ValueTask.CompletedTask; }
    }

    sealed class Transport : ICopilotRunTransport
    {
        public List<CopilotProcessRequest> Calls = [];
        public bool WrongRemote;
        public Task<CopilotProcessResult> ExecuteAsync(CopilotProcessRequest r, Action<string>? stdout, Action<string>? stderr, CancellationToken ct)
        {
            Calls.Add(r);
            var text = r.Script.Contains(InvestigationSource.DiscoverScript, StringComparison.Ordinal)
                ? "ADOINSPECT1\0Ubuntu\0/tmp/offline\0/tmp/offline/.git\0origin\0https://dev.azure.com/offline/Sample/_git/" + (WrongRemote ? "other" : "retry") + "\0"
                : r.Script.Contains(InvestigationSource.ReadScript, StringComparison.Ordinal) ? """{"lines":["line one","line two","line three"],"total":3}""" : "";
            return Task.FromResult(new CopilotProcessResult(0, text, ""));
        }
    }

    static async Task RunGitFixture()
    {
        var transport = new WslCopilotRunTransport();
        async Task<string> Shell(string script, params string[] args)
        {
            var result = await transport.ExecuteAsync(new("investigation-fixture", null, script, args), null, null, default);
            Check(result.ExitCode == 0, $"Synthetic Git operation failed (exit {result.ExitCode}): {result.StandardError}");
            return result.StandardOutput;
        }
        var directory = (await Shell("mktemp -d /tmp/ado-investigation-fixture.XXXXXXXXXX")).Trim();
        Check(System.Text.RegularExpressions.Regex.IsMatch(directory, @"\A/tmp/ado-investigation-fixture\.[a-zA-Z0-9]+\z"), "Invalid fixture path");
        var id = Guid.NewGuid().ToString("N");
        var work = directory + "/checkout/.git/ado-prview-investigations/" + id;
        try
        {
            await Shell("""
                set -euo pipefail
                mkdir -p "$1/home/.config/bash" "$1/home/.nvm"
                printf 'export GIT_SSH_COMMAND=synthetic-auth-command\nexport BASH_ENV="$HOME/extra-env"\n' > "$1/home/.config/bash/environment"
                printf 'export SSH_AUTH_SOCK=synthetic-agent-socket\n' > "$1/home/extra-env"
                printf 'touch "$HOME/nvm-executed"\nnvm() { return 59; }\n' > "$1/home/.nvm/nvm.sh"
                """, directory);
            var isolatedEnvironment = """
                export HOME="$1/home" PATH=/usr/bin:/bin
                unset ADO_PRVIEW_BASH_ENV BASH_ENV
                ! command -v copilot >/dev/null 2>&1
                """;
            var cliEnvironment = await transport.ExecuteAsync(new("investigation-fixture", null,
                isolatedEnvironment + "\n" + CopilotRunService.EnvironmentScript, [directory]), null, null, default);
            Check(cliEnvironment.ExitCode == 59, "Fixture did not reproduce failing CLI-specific NVM initialization");
            await Shell("test -f \"$1/home/nvm-executed\"; rm -- \"$1/home/nvm-executed\"", directory);
            await Shell(isolatedEnvironment + "\n" + CopilotRunService.SourceEnvironmentScript + "\n" + """
                [[ "$GIT_SSH_COMMAND" == synthetic-auth-command && "$SSH_AUTH_SOCK" == synthetic-agent-socket ]]
                [[ "$BASH_ENV" == "$HOME/extra-env" && "$GIT_TERMINAL_PROMPT" == 0 ]]
                command -v git >/dev/null; command -v python3 >/dev/null
                [[ ! -e "$HOME/nvm-executed" ]]
                """, directory);
            var commits = (await Shell("""
                set -euo pipefail
                base="$1"
                git init --quiet --bare "$base/upstream"
                git init --quiet -b release "$base/checkout"
                cd "$base/checkout"
                git config user.name "Offline fixture"; git config user.email "fixture@example.invalid"
                printf 'old behavior\n' > code.txt
                git add .; git -c core.hooksPath=/dev/null commit --quiet -m base
                basecommit="$(git rev-parse HEAD)"
                git switch --quiet -c topic
                printf 'new behavior\nline two\n' > code.txt
                printf '*.txt filter=fixture\n' > .gitattributes
                ln -s /etc/passwd outside
                printf 'version https://git-lfs.github.com/spec/v1\noid sha256:offline\n' > pointer
                python3 -c 'from pathlib import Path; Path("large").write_bytes(b"x"*80001)'
                python3 -c 'from pathlib import Path; Path("emoji").write_text(chr(0x1f431)*16000, encoding="utf-8"); d=Path("unicode"); d.mkdir(); [(d/(chr(0x1f431)*55+str(i))).touch() for i in range(200)]'
                git add .; git -c core.hooksPath=/dev/null commit --quiet -m source
                git remote add origin "$base/upstream"
                git push --quiet origin release topic
                git config filter.fixture.smudge "touch '$base/filter-executed'; cat"
                mkdir "$base/hooks"
                printf '#!/bin/sh\ntouch "%s/reference-hook-executed"\n' "$base" > "$base/hooks/reference-transaction"
                chmod +x "$base/hooks/reference-transaction"
                git config core.hooksPath "$base/hooks"
                printf '%s\n%s\n' "$(git rev-parse HEAD)" "$basecommit"
                """, directory)).Trim().Split('\n').Select(s => s.Trim()).ToArray();
            await Shell(InvestigationSource.PrepareScript, directory + "/checkout", "origin", "refs/heads/topic", "refs/heads/release",
                commits[0], commits[1], commits[1], id, work, directory + "/upstream");
            await Shell(InvestigationSource.VerifyScript, work, commits[0]);
            await Shell(InvestigationSource.VerifyRefsScript, directory + "/checkout", "origin", directory + "/upstream",
                "refs/heads/topic", "refs/heads/release", commits[0], commits[1]);
            async Task<JsonDocument> Read(string path, string commit, string op = "file") => JsonDocument.Parse(await Shell(
                InvestigationSource.ReadScript, work, commits[0], op, commit, path, "1"));
            using var after = await Read("/code.txt", commits[0]);
            using var before = await Read("/code.txt", commits[1]);
            Check(after.RootElement.GetProperty("lines")[0].GetString() == "new behavior" && before.RootElement.GetProperty("lines")[0].GetString() == "old behavior", "Pinned before/after content confused");
            foreach (var path in new[] { "/outside", "/pointer", "/large" })
            {
                using var omitted = await Read(path, commits[0]);
                Check(omitted.RootElement.TryGetProperty("omission", out _), "Unsafe/oversized content was read: " + path);
            }
            using var listing = await Read("/", commits[0], "directory");
            Check(listing.RootElement.GetProperty("entries").GetArrayLength() >= 4, "Directory discovery unavailable");
            using var unicodeFile = JsonDocument.Parse(await Shell("export PYTHONIOENCODING=ascii\n" + InvestigationSource.ReadScript,
                work, commits[0], "file", commits[0], "/emoji", "1"));
            Check(unicodeFile.RootElement.GetProperty("lines")[0].GetString()!.Length == 32000,
                "Accepted Unicode file window was truncated by ASCII JSON expansion");
            using var unicodeDirectory = JsonDocument.Parse(await Shell("export PYTHONIOENCODING=ascii\n" + InvestigationSource.ReadScript,
                work, commits[0], "directory", commits[0], "/unicode", "1"));
            Check(unicodeDirectory.RootElement.GetProperty("entries").GetArrayLength() == 200 &&
                unicodeDirectory.RootElement.GetProperty("entries")[199].GetString()!.Contains(char.ConvertFromUtf32(0x1f431)),
                "Accepted Unicode directory names were truncated by ASCII JSON expansion");
            await Shell("""
                set -euo pipefail
                [[ ! -e "$1/filter-executed" ]]
                [[ "$(git -C "$1/checkout" symbolic-ref --short HEAD)" == topic ]]
                [[ "$(cat "$1/checkout/code.txt")" == $'new behavior\nline two' ]]
                [[ -z "$(git -C "$1/checkout" status --porcelain)" ]]
                """, directory);
            await Shell(InvestigationSource.CleanupScript, directory + "/checkout", work, id, commits[0]);
            await Shell("test ! -e \"$1\" && test ! -e \"$2/reference-hook-executed\"", work, directory);
            await Shell(InvestigationSource.PrepareScript, directory + "/checkout", "origin", "refs/heads/topic", "refs/heads/release",
                commits[0], commits[1], commits[1], id, work, directory + "/upstream");
            await Shell("printf 'keep user files' > \"$1/user-added\"", work);
            var refused = await transport.ExecuteAsync(new("investigation-fixture", null, InvestigationSource.CleanupScript,
                [directory + "/checkout", work, id, commits[0]]), null, null, default);
            Check(refused.ExitCode != 0, "Cleanup removed user-added files");
            await Shell("test -f \"$1/user-added\"; rm -- \"$1/user-added\"", work);
            await Shell(InvestigationSource.CleanupScript, directory + "/checkout", work, id, commits[0]);
        }
        finally
        {
            await Shell("""
                set -euo pipefail
                [[ "$1" =~ ^/tmp/ado-investigation-fixture\.[a-zA-Z0-9]+$ ]]
                rm -rf -- "$1"
                """, directory);
        }
    }
}
