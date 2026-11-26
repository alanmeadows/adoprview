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
        await test("Investigation native default reads full pinned source and directory beyond diffs without WSL configuration", async () =>
        {
            using var f = new ExplainTests.Fixture();
            f.Store.Current.WslDistro = "Not installed";
            f.Store.Current.WslRepoDirectories[f.Store.Current.Repos.Single().Key] = "/missing/legacy/checkout";
            await using var source = await f.Service.GetInvestigationSourceAsync(f.Pr.Key, default);
            Check(source is AdoInvestigationSource && !source.Location.Contains("worktree", StringComparison.OrdinalIgnoreCase),
                "New investigation still requires a WSL/local worktree");
            var before = await source.ReadFileAsync("/RetryPolicy.cs", "before", 1, default);
            var after = await source.ReadFileAsync("/RetryPolicy.cs", "after", 1, default);
            var target = await source.ReadFileAsync("/RetryPolicy.cs", "target", 1, default);
            var broader = await source.ReadFileAsync("/README.md", "after", 1, default);
            var directory = await source.ListDirectoryAsync("/", "after", default);
            Check(before.Contains("FromSeconds(2)") && after.Contains("Math.Pow") && target.Contains(ExplainTests.Fixture.Target) &&
                broader.Contains("internal integration-test harness") && directory.Contains("/README.md"),
                "Native source lost base/source/target or broader architectural context");
            source.ValidateFinding(new("0", "Trigger", "Meaningful consequence", "/RetryPolicy.cs", 5,
                "        return TimeSpan.FromSeconds(Math.Min(300, 2 * Math.Pow(2, attempt)));"));
            await Throws<InvalidDataException>(() => Task.Run(() =>
                source.ValidateFinding(new("0", "Trigger", "Consequence", "/README.md", 1, "# Retry"))));
            await Throws<InvalidDataException>(() => Task.Run(() =>
                source.ValidateFinding(new("0", "Trigger", "Consequence", "/RetryPolicy.cs", 5, "        return TimeSpan.FromSeconds(2);"))));
            await source.Context.ReadChangeAsync(0, default);
            InvestigationModel.RequireInspection(source);
            await source.VerifyAsync(default);
            Check(f.Urls.Where(u => u.Contains("/items?")).All(u => u.Contains("versionDescriptor.versionType=commit")) &&
                InvestigationModel.Names.Contains("read_source_file") && !InvestigationModel.Names.Contains("read_worktree_file"),
                "Native review tool contract retained unpinned content or misleading filesystem tools");
        });
        await test("Investigation native default completes selected-only lens preview using actual API evidence and preserves target freshness", async () =>
        {
            using var f = new ExplainTests.Fixture();
            var writes = 0;
            var finding = new LensFinding("0", "Concrete failure", "Synthetic triggering input and consequence.",
                "/RetryPolicy.cs", 1, "public static class RetryPolicy");
            await using var service = new InvestigationService(f.Service.GetInvestigationSourceAsync,
                (key, revision, _, _, _, selected, _, _) =>
                {
                    writes++;
                    Check(key == f.Pr.Key && revision.SourceCommit == ExplainTests.Fixture.Source && selected.SequenceEqual([finding]),
                        "Native posting target or selection changed");
                    return Task.FromResult(new[] { new CommentOutcome("0", "posted", "Synthetic confirmed target", null) });
                }, new Model(async (source, _, _, _, ct) =>
                {
                    Check(source.Location.StartsWith("Azure DevOps:", StringComparison.Ordinal), "Production capture did not use authenticated native source");
                    await source.Context.ReadChangeAsync(0, ct);
                    await source.ReadFileAsync("/RetryPolicy.cs", "after", 1, ct);
                    source.ValidateFinding(finding);
                    InvestigationModel.RequireInspection(source);
                    return new("Synthetic private preview.", [finding]);
                }));
            var start = service.Ask(f.Pr.Key, "", "lens", "Check a concrete synthetic contract.");
            var done = await Finish(service);
            Check(done.Status == "completed" && writes == 0 && done.Evidence.Any(e => e.StartsWith("Source after:")),
                "Native generation failed or implicitly posted findings: " + done.Message);
            f.TargetHead = ExplainTests.Fixture.Base;
            done = await service.PostAsync(f.Pr.Key, start.ConversationId, start.RequestId, ["0"], true);
            Check(done.Status == "failed" && writes == 0, "Native source freshness no longer protects posting");
        });
        await test("Investigation native source blocks failed auth wrong revision and scope without accepting evidence", async () =>
        {
            foreach (var reason in new[] { "auth", "revision", "network", "directory" })
            {
                using var f = new ExplainTests.Fixture();
                await using var source = await InvestigationSource.CreateAsync(await f.Capture(), f.Pr, f.Store.Current, default);
                if (reason == "auth") f.DenyFiles = true;
                if (reason == "revision") f.WrongItem = true;
                if (reason == "network") f.NetworkFailure = true;
                if (reason == "directory")
                    f.AdditionalResponse = request => request.RequestUri!.AbsolutePath.EndsWith("/items")
                        ? Json(new { value = new[] { new { path = "/outside/secret.cs", commitId = ExplainTests.Fixture.Source } } }) : null;
                await Throws<Exception>(() => reason == "directory" ? source.ListDirectoryAsync("/within", "after", default) :
                    source.ReadFileAsync("/RetryPolicy.cs", "after", 1, default));
                Check(!source.Evidence.Any(e => e.StartsWith("Source ")), "Failed native source read became inspected evidence");
                await Throws<InvalidOperationException>(() => source.VerifyAsync(default));
            }
        });
        await test("Investigation native source omissions never validate symlink binary LFS oversized or partial quote evidence", async () =>
        {
            foreach (var reason in new[] { "symlink", "submodule", "binary", "lfs", "bytes", "window" })
            {
                using var f = new ExplainTests.Fixture();
                f.AdditionalResponse = request =>
                {
                    var uri = request.RequestUri!;
                    if (!uri.AbsolutePath.EndsWith("/items")) return null;
                    var query = uri.Query.TrimStart('?').Split('&').Select(s => s.Split('=', 2))
                        .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
                    return Json(new { path = "/RetryPolicy.cs", commitId = query["versionDescriptor.version"],
                        gitObjectType = reason == "submodule" ? "commit" : "blob", isSymLink = reason == "symlink",
                        contentMetadata = new { isBinary = reason == "binary" },
                        content = !query.ContainsKey("includeContent") ? null : reason switch
                        {
                            "lfs" => "version https://git-lfs.github.com/spec/v1\noid sha256:synthetic",
                            "bytes" => new string('\u00e9', 40001),
                            "window" => new string('x', 16001),
                            _ => "public static class RetryPolicy",
                        } });
                };
                await using var source = await InvestigationSource.CreateAsync(await f.Capture(), f.Pr, f.Store.Current, default);
                var output = await source.ReadFileAsync("/RetryPolicy.cs", "after", 1, default);
                Check(output.Contains("omitted", StringComparison.OrdinalIgnoreCase) || output.Contains("TRUNCATED"),
                    "Native source omission was hidden: " + reason);
                Check(!source.Evidence.Any(e => e.StartsWith("Source after:")), "Omitted or partial native content became exact finding proof");
                await Throws<InvalidDataException>(() => Task.Run(() =>
                    source.ValidateFinding(new("0", "Failure", "Consequence", "/RetryPolicy.cs", 1, "public static class RetryPolicy"))));
                await source.VerifyAsync(default);
            }
        });
        await test("Investigation native read budget and invalid paths fail explicitly before accepting further content", async () =>
        {
            using var f = new ExplainTests.Fixture();
            await using var source = await InvestigationSource.CreateAsync(await f.Capture(), f.Pr, f.Store.Current, default);
            var calls = f.Urls.Count;
            await Throws<ArgumentException>(() => source.ReadFileAsync("/../secret", "after", 1, default));
            await Throws<ArgumentException>(() => source.ReadFileAsync("/RetryPolicy.cs", "main", 1, default));
            Check(f.Urls.Count == calls, "Invalid source arguments reached ADO");
            for (var i = 0; i < 120; i++) await source.ListDirectoryAsync("/", "after", default);
            calls = f.Urls.Count;
            await Throws<InvalidOperationException>(() => source.ListDirectoryAsync("/", "after", default));
            Check(f.Urls.Count == calls, "Native read budget issued another source request");
            await Throws<InvalidOperationException>(() => source.VerifyAsync(default));
        });
        await test("Investigation explicit explanation section preserves full ARM host risks attribution and pinned selection without truncating the displayed artifact", async () =>
        {
            using var f = new ExplainTests.Fixture();
            const string before = "resource account 'Microsoft.Storage/storageAccounts@2023-05-01' = {\n  properties: {\n    allowBlobPublicAccess: true\n  }\n}";
            const string after = "resource account 'Microsoft.Storage/storageAccounts@2023-05-01' = {\n  properties: {\n    allowBlobPublicAccess: false\n  }\n}";
            ExplainTests.ArmFixture(f, "/deploy.bicep", before, after);
            await using var explain = new ExplainService((_, ct) => f.Capture(ct), new ExplanationModel(async (context, ct) =>
            {
                await context.ReadChangeAsync(0, ct);
                context.Arm.Record(0, "source", before, "source", after, "Disables anonymous access.",
                    "Anonymous consumers may fail; no deployment was run.", "allowBlobPublicAccess changes from true to false.");
                return "Unselected preamble.\n\n## Huge unrelated details\n" + new string('<', 20000) +
                    "\n\n## Focused question\nSmall useful explanation paragraph.\n\n```markdown\n## Fake fenced section\nkeep this code intact\n```\n" +
                    "\n~~~text\n# Another fake fenced section\n~~~\n\n## Other context\nHistorical detail.";
            }));
            var ready = explain.Start(f.Pr.Key);
            await CompleteExplanation(explain);
            var whole = explain.Artifacts.Single();
            var metadata = whole.ExplanationSections!;
            var focused = metadata.Single(s => s.Title == "Focused question");
            var arm = metadata.Single(s => s.Title == "Azure Resource Manager changes");
            Check(whole.Status == "completed" && metadata.All(s => !s.Title.Contains("Fake", StringComparison.OrdinalIgnoreCase)) &&
                metadata.All(s => s.Title is not ("Before" or "After" or "/deploy.bicep")) && focused.IncludesArm,
                "Fenced headings or ARM child fragments became selectable sections");
            var selections = new List<int?>();
            await using var service = new InvestigationService(async (_, ct) => new Source(await f.Capture(ct)),
                (_, _, _, _, _, _, _, _) => throw new Exception("Unexpected write"),
                new ContextModel((_, _, contexts, _, _, _, _) =>
                {
                    var attached = contexts.Single();
                    selections.Add(attached.SectionIndex);
                    Check(attached.Output.Contains(before) && attached.Output.Contains(after) && attached.Output.Contains("#### Changed lines") &&
                        attached.Output.Contains("Exact source excerpt") && attached.Output.Contains("**Risks and uncertainty:** Anonymous consumers may fail; no deployment was run."),
                        "Selected paragraph lost cohesive ARM Before/After, diff, attribution or risks");
                    Check(attached.Coverage.SequenceEqual(whole.Coverage) && attached.Evidence.SequenceEqual(whole.Evidence!) &&
                        attached.Receipt.Contains("complete host ARM section"), "Section attachment lost original coverage/evidence/receipt");
                    return Task.FromResult(new InvestigationAnswer("Grounded section answer.", []));
                }), explain.Resolve, resolveExplanationSection: explain.Resolve);
            await Throws<InvalidOperationException>(() => Task.FromResult(service.Ask(f.Pr.Key, "", "question", "Whole?", ready.RequestId)));
            foreach (var invalid in new[] { -1, 999 })
                await Throws<InvalidOperationException>(() => Task.FromResult(service.Ask(f.Pr.Key, "", "question", "Invalid section?", ready.RequestId, invalid)));
            await Throws<ArgumentException>(() => Task.FromResult(service.Ask(f.Pr.Key, "", "question", "Missing request?", explanationSectionIndex: focused.Index)));
            var start = service.Ask(f.Pr.Key, "", "question", "Use this paragraph explicitly.", ready.RequestId, focused.Index);
            var done = await Finish(service);
            var package = done.ExplanationContexts.Single();
            Check(done.Status == "completed" && package.SectionTitle == focused.Title && package.Output.Contains("Small useful explanation paragraph.") &&
                !package.Output.Contains(new string('<', 1000)) &&
                package.IncludedSectionIndices!.Contains(arm.Index) && package.OmittedSectionTitles!.Contains("Huge unrelated details") &&
                package.OmittedSectionTitles.Contains("Preamble before the first heading") &&
                done.Turns.Single().ExplanationSectionIndex == focused.Index, "Explicit section package/omissions/turn identity were not accepted exactly");
            service.Ask(f.Pr.Key, start.ConversationId, "question", "Continue with the same package.");
            done = await Finish(service);
            Check(done.Turns.All(t => t.ExplanationSectionIndex == focused.Index) && selections.SequenceEqual([focused.Index, focused.Index]),
                "Follow-up silently changed or dropped the pinned section");
            await Throws<InvalidOperationException>(() => Task.FromResult(service.Ask(f.Pr.Key, start.ConversationId, "question", "Swap section?", ready.RequestId, arm.Index)));
            await Throws<InvalidOperationException>(() => Task.FromResult(service.Ask(f.Pr.Key, start.ConversationId, "question", "Swap to entire?", ready.RequestId)));
            Check(selections.Count == 2 && explain.Artifacts.Single().Output == whole.Output, "Changing attachment silently replaced context or rewrote the displayed artifact");
            var reset = await service.ResetAsync(f.Pr.Key, start.ConversationId);
            service.Ask(f.Pr.Key, reset.ConversationId, "question", "Now use the ARM host explicitly.", ready.RequestId, arm.Index);
            done = await Finish(service);
            Check(done.Status == "completed" && done.Turns.Single().ExplanationSectionIndex == arm.Index, "New conversation could not explicitly change selection");
        });
        await test("Investigation explicit section still blocks when preserving the full ARM host exceeds escaped replay budget", async () =>
        {
            using var f = new ExplainTests.Fixture();
            var before = "resource account 'Microsoft.Storage/storageAccounts@2023-05-01' = {\n  name: '" + new string('<', 9000) +
                "'\n  properties: {\n    allowBlobPublicAccess: true\n  }\n}";
            var after = before.Replace("allowBlobPublicAccess: true", "allowBlobPublicAccess: false");
            ExplainTests.ArmFixture(f, "/deploy.bicep", before, after);
            await using var explain = new ExplainService((_, ct) => f.Capture(ct), new ExplanationModel(async (context, ct) =>
            {
                await context.ReadChangeAsync(0, ct);
                context.Arm.Record(0, "source", before, "source", after, "Disables anonymous access.", "Consumers may fail.", "Access changes.");
                return "## Small paragraph\nA small meaningful request context.";
            }));
            var ready = explain.Start(f.Pr.Key);
            await CompleteExplanation(explain);
            var state = explain.Artifacts.Single();
            var selected = state.ExplanationSections!.Single(s => s.Title == "Small paragraph");
            var package = explain.Resolve(f.Pr.Key, ready.RequestId, selected.Index);
            Check(state.Output.Length <= 64000 && package.Output.Contains(before) && package.Output.Contains(after) &&
                InvestigationModel.SerializeReplay([], [package], "question", "Compare?", ready.RequestId).Length > 100000,
                "Fixture did not require a whole oversized ARM host inside the chosen package");
            await using var service = new InvestigationService(async (_, ct) => new Source(await f.Capture(ct)),
                (_, _, _, _, _, _, _, _) => throw new Exception("Unexpected write"),
                new ContextModel((_, _, _, _, _, _, _) => throw new Exception("Oversized selected package reached model")),
                explain.Resolve, resolveExplanationSection: explain.Resolve);
            await Throws<InvalidOperationException>(() => Task.FromResult(service.Ask(f.Pr.Key, "", "question", "Compare?", ready.RequestId, selected.Index)));
            Check(service.States.Length == 0, "Oversized selected package accepted a phantom conversation or truncated ARM");
        });
        await test("Explain section metadata excludes fenced fake headings and refuses unclosed or unsectioned documents", async () =>
        {
            using var f = new ExplainTests.Fixture();
            var output = "## Real section\n```markdown\n## Fake inside longer fence\n````\n# Real following section\n~~~text\n## Fake tilde heading\n~~~\n";
            await using var explain = new ExplainService((_, ct) => f.Capture(ct), new ExplanationModel((_, _) => Task.FromResult(output)));
            explain.Start(f.Pr.Key);
            await CompleteExplanation(explain);
            var state = explain.Artifacts.Single();
            Check(state.ExplanationSections!.Select(s => s.Title).SequenceEqual(["Real section", "Real following section"]), "Fenced headings became selectable");
            output = "## Opening section\n```markdown\n## Not safely terminated";
            explain.Start(f.Pr.Key);
            await CompleteExplanation(explain);
            var unfinished = explain.Artifacts.Last();
            Check(unfinished.ExplanationSections!.Length == 0, "Unclosed code fence exposed an unsafe section");
            await Throws<InvalidOperationException>(() => Task.FromResult(explain.Resolve(f.Pr.Key, unfinished.RequestId, 0)));
            output = new string('<', 30000);
            explain.Start(f.Pr.Key);
            await CompleteExplanation(explain);
            var unsectioned = explain.Artifacts.Last();
            Check(unsectioned.ExplanationSections!.Length == 0, "Unsectioned text gained a fabricated selector");
            await Throws<InvalidOperationException>(() => Task.FromResult(explain.Resolve(f.Pr.Key, unsectioned.RequestId, 0)));
        });
        await test("Investigation reference PR URL resolves authenticated pinned evidence replays follow-ups and never changes posting target", async () =>
        {
            using var f = new ExplainTests.Fixture();
            var target = new Source(await f.Capture());
            ConfigureReference(f);
            var url = $"https://dev.azure.com/offline/{f.Pr.ProjectId}/_git/{f.Pr.RepoId}/pullrequest/456";
            var models = 0;
            var writes = 0;
            var finding = new LensFinding("0", "Reference compatibility failure", "Concrete comparison.", "/RetryPolicy.cs", 1, "public");
            await using var service = new InvestigationService((_, _) => Task.FromResult<IInvestigationSource>(target),
                (key, revision, _, _, _, selected, _, _) =>
                {
                    writes++;
                    Check(key == f.Pr.Key && revision.Id == 123 && selected.Single() with { OutgoingComment = null } == finding, "Reference replaced the posting target");
                    return Task.FromResult(new[] { new CommentOutcome("0", "posted", "Confirmed target comment", null) });
                }, new Model(async (source, history, kind, question, ct) =>
                {
                    models++;
                    Check(source.Context.Revision.Id == 123 && source.References.Single().Context.Revision.Id == 456, "Pinned target/reference identities mixed");
                    var reference = source.References.Single();
                    Check(reference.Snapshot().Output.Contains("ReferenceOnFailure") && reference.Snapshot().Revision.Title.Contains("Reference contract") &&
                        reference.Snapshot().Evidence.Any(e => e.StartsWith("Diff: ")), "URL was not actually read into attributable evidence");
                    Check((await reference.Context.ReadFileAsync("/RetryPolicy.cs", "after", 1, ct)).Contains("ReferenceOnFailure"),
                        "Bounded reference source inspection was unavailable");
                    Check(InvestigationModel.ReferenceTools(source.References, ct).Select(t => t.Name).SequenceEqual(InvestigationModel.ReferenceNames),
                        "Reference tools did not expose their exact restricted read-only inventory");
                    return new("Compared pinned target and reference source.", kind == "lens" ? [finding] : []);
                }), captureReference: f.Service.GetReferencePrContextAsync);
            var start = service.Ask(f.Pr.Key, "", "lens", "Aligned with PR " + url);
            var done = await Finish(service);
            Check(done.Status == "completed" && done.ReferencePrContexts.Single().Url == url &&
                done.Turns.Single().ReferencePrUrls!.SequenceEqual([url]), "Resolved reference state/turn association was not available: " + done.Message);
            done = await service.PostAsync(f.Pr.Key, start.ConversationId, start.RequestId, ["0"], true);
            Check(writes == 1 && done.Status == "completed", "Reference review lost exact target posting");
            service.Ask(f.Pr.Key, start.ConversationId, "question", "Why does that comparison matter?");
            done = await Finish(service);
            Check(models == 2 && done.ReferencePrContexts.Single().Revision.Id == 456 &&
                done.Turns.All(t => t.ReferencePrUrls!.SequenceEqual([url])), "Follow-up lost pinned reference evidence/turn association");
            await Throws<InvalidOperationException>(() => service.PostAsync(f.Pr.Key, start.ConversationId, start.RequestId, ["0"], true));
            Check(writes == 1, "Historical reference lens remained postable after another Ask");
            f.TargetHead = new string('d', 40);
            service.Ask(f.Pr.Key, start.ConversationId, "question", "Still aligned?");
            done = await Finish(service);
            Check(done.Status == "failed" && models == 2, "Changed target/reference source reached the model");
        });
        foreach (var status in new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound })
        await test("Investigation reference PR denied or missing is explicit and never reaches model " + status, async () =>
        {
            using var f = new ExplainTests.Fixture();
            var target = new Source(await f.Capture());
            f.AdditionalResponse = request => request.RequestUri!.AbsolutePath.EndsWith("/repositories/" + f.Pr.RepoId)
                ? new HttpResponseMessage(status) { Content = new StringContent("""{"message":"synthetic denied reference"}""") } : null;
            await using var service = new InvestigationService((_, _) => Task.FromResult<IInvestigationSource>(target),
                (_, _, _, _, _, _, _, _) => throw new Exception("Unexpected write"),
                new Model((_, _, _, _, _) => throw new Exception("Unread reference reached model")), captureReference: f.Service.GetReferencePrContextAsync);
            service.Ask(f.Pr.Key, "", "question", $"Aligned with PR https://dev.azure.com/offline/{f.Pr.ProjectId}/_git/{f.Pr.RepoId}/pullrequest/456");
            var done = await Finish(service);
            Check(done.Status == "failed" && done.Message.Contains(status == HttpStatusCode.Forbidden ? "access denied" : "not found") &&
                done.Turns.Length == 0 && done.ReferencePrContexts.Length == 0, "Unread reference was accepted without a precise error");
        });
        await test("Investigation reference PR rejects unsupported hosts and oversized evidence without model calls", async () =>
        {
            using var f = new ExplainTests.Fixture();
            var target = new Source(await f.Capture());
            ConfigureReference(f, oversized: true);
            var captures = 0;
            await using var service = new InvestigationService((_, _) => Task.FromResult<IInvestigationSource>(target),
                (_, _, _, _, _, _, _, _) => throw new Exception("Unexpected write"),
                new Model((_, _, _, _, _) => throw new Exception("Oversized reference reached model")),
                captureReference: async (url, ct) => { captures++; return await f.Service.GetReferencePrContextAsync(url, ct); });
            foreach (var url in new[] { "https://example.invalid/policy", "https://dev.azure.com.evil.invalid/o/p/_git/r/pullrequest/1",
                "https://user:secret@dev.azure.com/o/p/_git/r/pullrequest/1", "http://dev.azure.com/o/p/_git/r/pullrequest/1",
                "https://dev.azure.com/o/p/_git/r/pullrequest/1?redirect=https://evil.invalid", "https://dev.azure.com/o/p/_git/r/pullrequest/0" })
                await Throws<ArgumentException>(() => Task.FromResult(service.Ask(f.Pr.Key, "", "lens", "Aligned with " + url)));
            Check(captures == 0 && service.States.Length == 0, "Unsupported host/URL escaped reference validation");
            service.Ask(f.Pr.Key, "", "lens", $"Aligned with https://dev.azure.com/offline/{f.Pr.ProjectId}/_git/{f.Pr.RepoId}/pullrequest/456");
            var done = await Finish(service);
            Check(done.Status == "failed" && done.Message.Contains("size limit") && done.ReferencePrContexts.Length == 0,
                "Oversized reference diff was silently truncated");
            Check(ReferencePrContext.Canonical("https://sample.visualstudio.com/DefaultCollection/Project/_git/Repo/pullrequest/5") ==
                "https://dev.azure.com/sample/Project/_git/Repo/pullrequest/5", "Standard legacy ADO URL was not canonicalized");
        });
        foreach (var sourceMoves in new[] { true, false })
        await test($"Investigation reference PR rechecks distinct reference source and target branches before follow-up and posting (source={sourceMoves})", async () =>
        {
            using var f = new ExplainTests.Fixture();
            var target = new Source(await f.Capture());
            var moved = false;
            ConfigureReference(f, drift: () => moved ? sourceMoves ? "source" : "target" : "");
            var calls = 0;
            var writes = 0;
            await using var service = new InvestigationService((_, _) => Task.FromResult<IInvestigationSource>(target),
                (_, _, _, _, _, _, _, _) => { writes++; throw new Exception("Stale reference reached posting"); },
                new Model((_, _, _, _, _) =>
                { calls++; return Task.FromResult(new InvestigationAnswer("Preview", [new("0", "Meaningful", "Consequence", "/RetryPolicy.cs", 1, "public")])); }),
                captureReference: f.Service.GetReferencePrContextAsync);
            var start = service.Ask(f.Pr.Key, "", "lens", $"Aligned with PR https://dev.azure.com/offline/{f.Pr.ProjectId}/_git/{f.Pr.RepoId}/pullrequest/456");
            Check((await Finish(service)).Status == "completed", "Initial reference preview did not complete");
            moved = true;
            var blocked = await service.PostAsync(f.Pr.Key, start.ConversationId, start.RequestId, ["0"], true);
            Check(blocked.Status == "failed" && writes == 0, "Reference branch drift reached a write");
            service.Ask(f.Pr.Key, start.ConversationId, "question", "Still aligned?");
            blocked = await Finish(service);
            Check(blocked.Status == "failed" && calls == 1 && blocked.Turns.Length == 1 && blocked.ReferencePrContexts.Single().Revision.Id == 456,
                "Reference drift reached a follow-up or silently rebased the prior reference");
        });
        await test("Investigation reference PR drift during generation accepts neither answer nor reference context", async () =>
        {
            using var f = new ExplainTests.Fixture();
            var target = new Source(await f.Capture());
            var moved = false;
            ConfigureReference(f, drift: () => moved ? "source" : "");
            await using var service = new InvestigationService((_, _) => Task.FromResult<IInvestigationSource>(target),
                (_, _, _, _, _, _, _, _) => throw new Exception("Unexpected write"),
                new Model((_, _, _, _, _) => { moved = true; return Task.FromResult(new InvestigationAnswer("Unverified answer", [])); }),
                captureReference: f.Service.GetReferencePrContextAsync);
            service.Ask(f.Pr.Key, "", "question", $"Aligned with PR https://dev.azure.com/offline/{f.Pr.ProjectId}/_git/{f.Pr.RepoId}/pullrequest/456");
            var done = await Finish(service);
            Check(done.Status == "failed" && done.Turns.Length == 0 && done.ReferencePrContexts.Length == 0,
                "Reference drift during generation became a verified answer");
        });
        await test("Investigation explanation bridge replays immutable E1 on initial and follow-up calls without synthetic turns", async () =>
        {
            using var f = new ExplainTests.Fixture();
            var generation = 0;
            await using var explain = new ExplainService((_, ct) => f.Capture(ct),
                new ExplanationModel((_, _) => Task.FromResult($"Recognizable E{++generation}: safer is a claim to challenge.")));
            var e1 = explain.Start(f.Pr.Key);
            await CompleteExplanation(explain);
            var observed = new List<string>();
            await using var service = new InvestigationService(async (_, ct) => new Source(await f.Capture(ct)),
                (_, _, _, _, _, _, _, _) => throw new Exception("Unexpected write"),
                new ContextModel((_, turns, contexts, selected, _, question, _) =>
                {
                    Check(turns.Length == observed.Count, "Explanation import manufactured a completed turn");
                    observed.Add(InvestigationModel.SerializeReplay(turns, contexts, "question", question, selected));
                    return Task.FromResult(new InvestigationAnswer("Grounded challenge to E1.", []));
                }), explain.Resolve);
            var first = service.Ask(f.Pr.Key, "", "question", "Why safer?", e1.RequestId);
            var done = await Finish(service);
            Check(done.Turns.Single().ExplanationRequestId == e1.RequestId && done.ExplanationContexts.Single().Output.Contains("Recognizable E1"),
                "Accepted answer lost its exact explanation identity/content");
            var e2 = explain.Start(f.Pr.Key);
            await CompleteExplanation(explain);
            Check(explain.Resolve(f.Pr.Key, e1.RequestId).Output.Contains("Recognizable E1"), "Rerun relabelled E1");
            var publicCopy = explain.Artifacts;
            publicCopy[0].Coverage[0] = "external mutation";
            Check(!explain.Resolve(f.Pr.Key, e1.RequestId).Coverage.Contains("external mutation"), "Completed artifact was externally mutable");
            service.Ask(f.Pr.Key, first.ConversationId, "question", "Challenge that claim again.");
            done = await Finish(service);
            Check(observed.Count == 2 && observed.All(s => s.Contains("Recognizable E1")) && !observed[1].Contains("Recognizable E2") &&
                done.Turns.All(t => t.ExplanationRequestId == e1.RequestId), "Follow-up silently used newest Explain or dropped E1");
            service.Ask(f.Pr.Key, first.ConversationId, "question", "Compare with E2.", e2.RequestId);
            done = await Finish(service);
            Check(done.ExplanationContexts.Length == 2 && done.Turns.Last().ExplanationRequestId == e2.RequestId &&
                done.Turns.Last().ExplanationRequestIds!.SequenceEqual([e1.RequestId, e2.RequestId]) &&
                done.Turns[0].ExplanationRequestIds!.SequenceEqual([e1.RequestId]) &&
                observed.Last().Contains("Recognizable E1") && observed.Last().Contains("Recognizable E2"), "New explicit E2 silently replaced E1 or relabelled old turn associations");
            for (var i = 0; i < 12; i++) { explain.Start(f.Pr.Key); await CompleteExplanation(explain); }
            Check(explain.Artifacts.Length == 12, "Completed artifact registry was not bounded");
            await Throws<InvalidOperationException>(() => Task.FromResult(explain.Resolve(f.Pr.Key, e1.RequestId)));
            service.Ask(f.Pr.Key, first.ConversationId, "question", "Return to pinned E1.", e1.RequestId);
            done = await Finish(service);
            Check(done.Status == "completed" && observed.Last().Contains("Recognizable E1") &&
                done.Turns.Last().ExplanationRequestId == e1.RequestId, "Registry expiry evicted an already attached server context");
        });
        await test("Investigation explanation bridge rejects missing unfinished cancelled failed and cross-PR contexts before model", async () =>
        {
            using var f = new ExplainTests.Fixture();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var explain = new ExplainService((_, ct) => f.Capture(ct), new ExplanationModel(async (_, ct) =>
            { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return "unreachable"; }));
            var running = explain.Start(f.Pr.Key);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var calls = 0;
            await using var service = new InvestigationService(async (_, ct) => new Source(await f.Capture(ct)),
                (_, _, _, _, _, _, _, _) => throw new Exception("Unexpected write"),
                new ContextModel((_, _, _, _, _, _, _) => { calls++; return Task.FromResult(new InvestigationAnswer("Unexpected", [])); }), explain.Resolve);
            foreach (var id in new[] { "missing", running.RequestId })
                await Throws<InvalidOperationException>(() => Task.FromResult(service.Ask(f.Pr.Key, "", "question", "Attach?", id)));
            await explain.CancelAsync(f.Pr.Key, running.RequestId);
            await Throws<InvalidOperationException>(() => Task.FromResult(service.Ask(f.Pr.Key, "", "question", "Cancelled?", running.RequestId)));
            await using var failed = new ExplainService((_, ct) => f.Capture(ct), new ExplanationModel((_, _) => throw new InvalidOperationException("offline failure")));
            var failure = failed.Start(f.Pr.Key);
            await CompleteExplanation(failed);
            await Throws<InvalidOperationException>(() => Task.FromResult(failed.Resolve(f.Pr.Key, failure.RequestId)));
            await using var completed = new ExplainService((_, ct) => f.Capture(ct), new ExplanationModel((_, _) => Task.FromResult("Finished E1")));
            var ready = completed.Start(f.Pr.Key);
            await CompleteExplanation(completed);
            await Throws<InvalidOperationException>(() => Task.FromResult(completed.Resolve("different-pr", ready.RequestId)));
            Check(calls == 0 && service.States.Length == 0 && explain.Artifacts.Length == 0 && failed.Artifacts.Length == 0,
                "Invalid attachment reached model or cancellation accepted a completed artifact");
        });
        await test("Investigation explanation bridge checks every captured revision identity field and source freshness before model", async () =>
        {
            using var f = new ExplainTests.Fixture();
            await using var explain = new ExplainService((_, ct) => f.Capture(ct), new ExplanationModel((_, _) => Task.FromResult("Pinned E1")));
            var ready = explain.Start(f.Pr.Key);
            await CompleteExplanation(explain);
            var attachment = explain.Resolve(f.Pr.Key, ready.RequestId);
            var r = attachment.Revision;
            foreach (var revision in new[] { r with { Id = r.Id + 1 }, r with { Title = "changed" }, r with { Repo = "changed" },
                r with { SourceCommit = "changed" }, r with { BaseCommit = "changed" }, r with { Iteration = r.Iteration + 1 },
                r with { TargetCommit = "changed" }, r with { SourceRef = "changed" }, r with { TargetRef = "changed" } })
            {
                await using var service = new InvestigationService(async (_, ct) => new Source(await f.Capture(ct)),
                    (_, _, _, _, _, _, _, _) => throw new Exception("Unexpected write"),
                    new ContextModel((_, _, _, _, _, _, _) => throw new Exception("Mismatched revision reached model")),
                    (_, _) => attachment with { Revision = revision });
                service.Ask(f.Pr.Key, "", "question", "Same key is not enough?", ready.RequestId);
                var done = await Finish(service);
                Check(done.Status == "failed" && done.Message.Contains("revisions changed") && done.Turns.Length == 0 &&
                    done.ExplanationContexts.Length == 0, "Revision mismatch accepted an attachment");
            }
            var source = new Source(await f.Capture());
            f.MovedSource = true;
            await using var stale = new InvestigationService((_, _) => Task.FromResult<IInvestigationSource>(source),
                (_, _, _, _, _, _, _, _) => throw new Exception("Unexpected write"),
                new ContextModel((_, _, _, _, _, _, _) => throw new Exception("Stale source reached model")), explain.Resolve);
            stale.Ask(f.Pr.Key, "", "question", "Still current?", ready.RequestId);
            Check((await Finish(stale)).Status == "failed", "Changed live source was accepted");
        });
        await test("Investigation explanation bridge accounts for whole 64k output JSON escaping incoming question answer and twelve-turn budgets", async () =>
        {
            using var f = new ExplainTests.Fixture();
            var character = 'e';
            await using var explain = new ExplainService((_, ct) => f.Capture(ct), new ExplanationModel((_, _) => Task.FromResult(new string(character, 64000))));
            var ready = explain.Start(f.Pr.Key);
            await CompleteExplanation(explain);
            var calls = 0;
            await using var service = new InvestigationService(async (_, ct) => new Source(await f.Capture(ct)),
                (_, _, _, _, _, _, _, _) => throw new Exception("Unexpected write"),
                new ContextModel((_, _, contexts, _, _, _, _) =>
                { calls++; Check(contexts.Single().Output.Length == 64000, "Whole Explain was truncated"); return Task.FromResult(new InvestigationAnswer(new string('a', 32000), [])); }), explain.Resolve);
            var first = service.Ask(f.Pr.Key, "", "question", "Full context?", ready.RequestId);
            var done = await Finish(service);
            Check(done.Status == "completed" && done.Turns.Length == 1, "Bounded 64k Explain plus max answer did not fit");
            await Throws<InvalidOperationException>(() => Task.FromResult(service.Ask(f.Pr.Key, first.ConversationId, "question", new string('<', 4000))));
            Check(calls == 1, "Incoming escaped question exceeded budget before the model");
            service.Ask(f.Pr.Key, first.ConversationId, "question", "Small follow-up.");
            done = await Finish(service);
            Check(done.Status == "failed" && done.Message.Contains("context limit") && done.Turns.Length == 1 &&
                done.ExplanationContexts.Single().Output.Length == 64000, "Oversized answer silently lost prior whole context or was accepted");
            character = '<';
            var large = explain.Start(f.Pr.Key);
            await CompleteExplanation(explain);
            var escapedAttachment = explain.Resolve(f.Pr.Key, large.RequestId);
            var overhead = InvestigationModel.SerializeReplay([], [escapedAttachment with { Output = "" }], "question", "Escaped?", large.RequestId).Length;
            var ordinarySize = InvestigationModel.SerializeReplay([], [escapedAttachment with { Output = new string('e', 64000) }], "question", "Escaped?", large.RequestId).Length;
            var escapedSize = InvestigationModel.SerializeReplay([], [escapedAttachment], "question", "Escaped?", large.RequestId).Length;
            Check(ordinarySize == overhead + 64000 && escapedSize == overhead + 384000, "Escaping characterization did not use the actual replay encoder");
            Console.WriteLine($"Fresh Explain replay sample: metadata/request overhead={overhead}; 64k ordinary ASCII={ordinarySize}; 64k '<'={escapedSize}; " +
                $"maximum '<' characters before request-only overflow={(100000 - overhead) / 6}; " +
                $"64k mixed ASCII maximum '<' before request-only overflow={(100000 - overhead - 64000) / 5}.");
            await using var escaped = new InvestigationService(async (_, ct) => new Source(await f.Capture(ct)),
                (_, _, _, _, _, _, _, _) => throw new Exception("Unexpected write"),
                new ContextModel((_, _, _, _, _, _, _) => throw new Exception("Escaped oversize reached model")),
                explain.Resolve);
            await Throws<InvalidOperationException>(() => Task.FromResult(escaped.Ask(f.Pr.Key, "", "question", "Escaped?", large.RequestId)));
            Check(escaped.States.Length == 0, "Oversized attachment reserved a phantom conversation");
            await using var small = new InvestigationService(async (_, ct) => new Source(await f.Capture(ct)),
                (_, _, _, _, _, _, _, _) => throw new Exception("Unexpected write"),
                new Model((_, _, _, _, _) => Task.FromResult(new InvestigationAnswer("Bounded.", []))));
            var start = small.Ask(f.Pr.Key, "", "question", "Turn 1");
            await Finish(small);
            for (var i = 2; i <= 12; i++) { small.Ask(f.Pr.Key, start.ConversationId, "question", "Turn " + i); await Finish(small); }
            await Throws<InvalidOperationException>(() => Task.FromResult(small.Ask(f.Pr.Key, start.ConversationId, "question", "Turn 13")));
            Check(small.States.Single().Turns.Length == 12, "Twelve-turn budget truncated history");
        });
        await test("Investigation cancellation does not accept pending explanation context or invalidate prior lens identity", async () =>
        {
            using var f = new ExplainTests.Fixture();
            await using var explain = new ExplainService((_, ct) => f.Capture(ct), new ExplanationModel((_, _) => Task.FromResult("E1")));
            var ready = explain.Start(f.Pr.Key);
            await CompleteExplanation(explain);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var service = new InvestigationService(async (_, ct) => new Source(await f.Capture(ct)),
                (_, _, _, _, _, _, _, _) => throw new Exception("Unexpected write"),
                new ContextModel(async (_, _, _, _, _, _, ct) =>
                { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return new("unreachable", []); }), explain.Resolve);
            var start = service.Ask(f.Pr.Key, "", "question", "Cancel imported E1?", ready.RequestId);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var cancelled = await service.CancelAsync(f.Pr.Key, start.ConversationId, start.RequestId);
            Check(cancelled.Status == "cancelled" && cancelled.Turns.Length == 0 && cancelled.ExplanationContexts.Length == 0,
                "Cancelled answer imported context as an accepted turn");
        });
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
            Check(postOutcomes ? JsonSerializer.Serialize(done.Turns, Bridge.Json).Length > 100000 :
                done.Status == "failed" && done.Turns.Length == 1 && done.Message.Contains("context limit"),
                "Oversized response/outcomes were not bounded explicitly");
            var before = requests;
            if (postOutcomes)
            {
                await Throws<InvalidOperationException>(() => Task.FromResult(service.Ask(f.Pr.Key, start.ConversationId, "question", "Too much history")));
                Check(requests == before, "Oversized history reached the model");
            }
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
                    Check(selected.Length == 1 && selected[0] with { OutgoingComment = null } == finding, "Unselected findings posted");
                    return Task.FromResult(new[] { new CommentOutcome("0", "posted", "confirmed", "https://dev.azure.com/offline/thread") });
                }, new Model((_, _, _, _, _) => Task.FromResult(new InvestigationAnswer("Scoped preview.", [finding, finding with { Id = "1" }]))));
            var start = service.Ask(f.Pr.Key, "", "lens", "Does this work with the caller?");
            var done = await Finish(service);
            Check(calls == 0 && done.Turns[0].Findings.Length == 2, "Lens generation wrote to ADO");
            Check(done.Turns[0].Findings[0].OutgoingComment == PrService.LensCommentContent(source.Context.Revision,
                start.ConversationId, start.RequestId, "Does this work with the caller?", finding),
                "Confirmation preview differs from the full host-formatted outgoing comment");
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
                Check(content == PrService.LensCommentContent(context.Revision, "conversation", "turn",
                    "[label](https://example.invalid/target)", new LensFinding((posts - 1).ToString(), "Failure",
                    "![hidden](https://example.invalid/image) <img src='https://example.invalid/html'>", "/RetryPolicy.cs", 2, "{")),
                    "Actual outgoing text differs from the server preview formatter");
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
            var absent = await source.ListDirectoryAsync("/absent", "before", default);
            Check(absent.Contains("omission") && source.Evidence.Any(e => e.Contains("directory /absent: not a tracked directory")) &&
                !source.Evidence.Contains("Worktree before: directory /absent (names only)."),
                "Absent directory was reported as inspected names instead of an explicit omission");
            await source.ReadFileAsync("/RetryPolicy.cs", "after", 1, default);
            await source.VerifyAsync(default);
            await source.DisposeAsync();
            Check(transport.Calls.Count >= 9 && transport.Calls.All(call =>
                call.Script.StartsWith(CopilotRunService.SourceEnvironmentScript + "\n", StringComparison.Ordinal) &&
                call.Script.IndexOf(CopilotRunService.SourceEnvironmentScript, StringComparison.Ordinal) ==
                call.Script.LastIndexOf(CopilotRunService.SourceEnvironmentScript, StringComparison.Ordinal) &&
                !call.Script.Contains("nvm use", StringComparison.Ordinal) &&
                call.Script.Contains("export GIT_NO_REPLACE_OBJECTS=1", StringComparison.Ordinal)),
                "Fresh source shells lost the authenticated environment or initialized it twice");
        });
        if (Environment.GetEnvironmentVariable("ADOPRVIEW_TEST_WSL_INVESTIGATION") == "1")
            await test("Investigation synthetic Git worktree readers never run filters or follow symlinks and preserve primary checkout", RunGitFixture);
    }

    static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    sealed class Model(Func<IInvestigationSource, InvestigationTurn[], string, string, CancellationToken, Task<InvestigationAnswer>> run) : IInvestigationModel
    {
        public Task<InvestigationAnswer> AskAsync(IInvestigationSource source, InvestigationTurn[] history, string kind, string question, CancellationToken ct,
            ExplanationAttachment[]? explanationContexts = null, string? explanationRequestId = null) => run(source, history, kind, question, ct);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    static void ConfigureReference(ExplainTests.Fixture f, bool oversized = false, Func<string>? drift = null)
    {
        f.AdditionalResponse = request =>
        {
            Check(request.Method == HttpMethod.Get, "Reference attempted a mutation");
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/repositories/" + f.Pr.RepoId))
                return Json(new { id = f.Pr.RepoId, name = f.Pr.Repo, project = new { id = f.Pr.ProjectId, name = f.Pr.Project } });
            if (path.EndsWith("/pullrequests/456"))
                return Json(new { pullRequestId = 456, title = "Reference contract distinct from target", status = "active",
                    sourceRefName = "refs/heads/reference", targetRefName = "refs/heads/reference-target",
                    repository = new { id = f.Pr.RepoId, project = new { id = f.Pr.ProjectId } },
                    lastMergeSourceCommit = new { commitId = ExplainTests.Fixture.Source } });
            if (path.EndsWith("/refs"))
            {
                var filter = request.RequestUri.Query.TrimStart('?').Split('&').Single(p => p.StartsWith("filter="))["filter=".Length..];
                var name = "refs/" + Uri.UnescapeDataString(filter);
                if (name is "refs/heads/reference" or "refs/heads/reference-target")
                {
                    var source = name == "refs/heads/reference";
                    var moved = drift?.Invoke() == (source ? "source" : "target");
                    return Json(new { value = new[] { new { name, objectId = moved ? new string('d', 40) :
                        source ? ExplainTests.Fixture.Source : ExplainTests.Fixture.Target } } });
                }
            }
            if (path.Contains("/pullrequests/456/") && path.EndsWith("/changes"))
                return Json(new { changeEntries = new[] { new { item = new { path = "/RetryPolicy.cs" }, changeType = "edit" } }, nextSkip = 0 });
            if (path.EndsWith("/items") && request.RequestUri.Query.Contains("includeContent"))
            {
                var query = request.RequestUri.Query.TrimStart('?').Split('&').Select(s => s.Split('=', 2))
                    .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
                var commit = query["versionDescriptor.version"];
                return Json(new { path = "/RetryPolicy.cs", commitId = commit, gitObjectType = "blob",
                    contentMetadata = new { isBinary = false },
                    content = oversized ? new string('x', 80001) : commit == ExplainTests.Fixture.Base ? "ReferenceBefore();" : "ReferenceOnFailure();" });
            }
            return null;
        };
    }

    sealed class ContextModel(Func<IInvestigationSource, InvestigationTurn[], ExplanationAttachment[], string?, string, string, CancellationToken, Task<InvestigationAnswer>> run) : IInvestigationModel
    {
        public Task<InvestigationAnswer> AskAsync(IInvestigationSource source, InvestigationTurn[] history, string kind, string question, CancellationToken ct,
            ExplanationAttachment[]? explanationContexts = null, string? explanationRequestId = null) =>
            run(source, history, explanationContexts ?? [], explanationRequestId, kind, question, ct);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class ExplanationModel(Func<ExplainContext, CancellationToken, Task<string>> run) : IExplainModel
    {
        public Task<string> ExplainAsync(ExplainContext context, Action<string> stream, CancellationToken ct) => run(context, ct);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    static async Task CompleteExplanation(ExplainService service)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (service.States.Any(s => ExplainService.Active(s.Status)) && DateTime.UtcNow < until) await Task.Delay(10);
        Check(!service.States.Any(s => ExplainService.Active(s.Status)), "Explanation did not finish");
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
                : r.Script.Contains(InvestigationSource.ReadScript, StringComparison.Ordinal)
                    ? r.Arguments[4] == "/absent"
                        ? """{"entries":[],"omission":"not a tracked directory at the captured revision"}"""
                        : """{"lines":["line one","line two","line three"],"total":3}"""
                    : "";
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
                python3 -c 'from pathlib import Path; Path("wide-window").write_text(("x"*200+"\n")*120, encoding="utf-8")'
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
            await Shell("""
                set -euo pipefail
                cd "$1/checkout"
                original="$(git rev-parse "$2:code.txt")"
                replacement="$(printf 'replacement behavior\nline two\n' | git hash-object -w --stdin)"
                git -c core.hooksPath=/dev/null replace "$original" "$replacement"
                """, directory, commits[0]);
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
            await Shell("""
                set -euo pipefail
                mkdir "$1/python-ambient"
                printf 'open("%s/python-import-executed", "w").write("unexpected import")\nraise RuntimeError("ambient code executed")\n' "$1" > "$1/python-ambient/json.py"
                cp -- "$1/python-ambient/json.py" "$1/python-ambient/sitecustomize.py"
                """, directory);
            using var isolatedImport = JsonDocument.Parse(await Shell("""
                cd -- "$1/python-ambient"
                export PYTHONPATH="$PWD"
                shift
                """ + "\n" + InvestigationSource.ReadScript, directory, work, commits[0], "file", commits[0], "/code.txt", "1"));
            Check(isolatedImport.RootElement.GetProperty("lines")[0].GetString() == "new behavior",
                "Ambient Python modules changed pinned source");
            await Shell("test ! -e \"$1/python-import-executed\"", directory);
            foreach (var path in new[] { "/outside", "/pointer", "/large", "/wide-window" })
            {
                using var omitted = await Read(path, commits[0]);
                Check(omitted.RootElement.TryGetProperty("omission", out _), "Unsafe/oversized content was read: " + path);
            }
            using var listing = await Read("/", commits[0], "directory");
            Check(listing.RootElement.GetProperty("entries").GetArrayLength() >= 4, "Directory discovery unavailable");
            foreach (var (path, commit) in new[] { ("/absent", commits[0]), ("/unicode", commits[1]), ("/code.txt", commits[0]) })
            {
                using var absent = await Read(path, commit, "directory");
                Check(absent.RootElement.GetProperty("entries").GetArrayLength() == 0 &&
                    absent.RootElement.TryGetProperty("omission", out _), "Missing/non-directory lookup did not return a truthful omission");
            }
            using var continued = await Read("/code.txt", commits[0]);
            Check(continued.RootElement.GetProperty("lines")[0].GetString() == "new behavior", "Directory absence blocked a subsequent valid read");
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
