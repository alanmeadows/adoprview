using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AdoPrView;
using GitHub.Copilot;

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
        const string bicepBefore = "resource account 'Microsoft.Storage/storageAccounts@2023-05-01' = {\n  name: 'sample'\n  properties: {\n    allowBlobPublicAccess: true\n  }\n}";
        const string bicepAfter = "resource account 'Microsoft.Storage/storageAccounts@2023-05-01' = {\n  name: 'sample'\n  properties: {\n    allowBlobPublicAccess: false\n  }\n}";
        await test("Explain ARM source examples survive capture model and final output with exact diff and assessment", async () =>
        {
            using var f = new Fixture();
            ArmFixture(f, "/deploy.bicep", bicepBefore, bicepAfter);
            await using var service = new ExplainService((_, ct) => f.Capture(ct), new Model(async (context, _, ct) =>
            {
                await context.ReadChangeAsync(0, ct);
                context.Arm.Record(0, "source", bicepBefore, "source", bicepAfter,
                    "Disables public blob access for this account.", "Consumers relying on anonymous access may fail; no deployment was run.",
                    "allowBlobPublicAccess changes from true to false.");
                return "The storage deployment disables public blob access.";
            }));
            service.Start(f.Pr.Key);
            var result = await Finish(service);
            Check(result.Status == "completed" && result.Output.Contains("#### Before") && result.Output.Contains("#### After"), "ARM output did not complete");
            Check(result.Output.Contains("```bicep\n" + bicepBefore) && result.Output.Contains("```bicep\n" + bicepAfter), "Exact source examples changed");
            Check(result.Output.Contains("-    allowBlobPublicAccess: true\n+    allowBlobPublicAccess: false") &&
                result.Output.Contains("**Improvement:**") && result.Output.Contains("**Risks and uncertainty:**") &&
                result.Output.Contains("Exact source excerpt"), "Missing visible differences, attribution or assessment");
            if (Environment.GetEnvironmentVariable("ADOPRVIEW_ARM_EXAMPLE") is { Length: > 0 } examplePath)
                File.WriteAllText(examplePath, result.Output);
        });
        await test("Explain ARM synthesis tool does not alter shared question lens or video source tools", async () =>
        {
            using var f = new Fixture();
            using var context = await f.Capture();
            var shared = ExplainModel.SourceTools(context, default);
            var tools = ExplainModel.SourceTools(context, default, arm: true);
            Check(shared.Select(t => t.Name).SequenceEqual(ExplainModel.ToolNames), "Shared read-only tool inventory changed");
            Check(tools.Select(t => t.Name).SequenceEqual(ExplainModel.ToolNames.Append("explain_arm_change")), "Explain ARM tool missing");
            Check(ExplainModel.RestrictedSession("offline", tools, arm: true).AvailableTools!.SequenceEqual(tools.Select(t => t.Name)),
                "ARM session allowlist does not match registered tools");
        });
        await test("Explain ARM cannot complete with missing records and non-ARM output stays unchanged", async () =>
        {
            using var f = new Fixture();
            ArmFixture(f, "/deploy.bicep", bicepBefore, bicepAfter);
            await using var service = new ExplainService((_, ct) => f.Capture(ct), new Model((_, _, _) => Task.FromResult("Looks better.")));
            service.Start(f.Pr.Key);
            var result = await Finish(service);
            Check(result.Status == "failed" && result.Message.Contains("ARM explanation missing"), "Missing ARM examples accepted as complete");
            var arm = new ExplainArm();
            var ordinary = new ExplainChange(0, "/model.go", null, "edit", false);
            arm.Observe(ordinary, "type Request struct { Name string }", "type Request struct { Name string; Count int }");
            Check(arm.Complete("Ordinary request field.", [ordinary]) == "Ordinary request field.", "Fabricated ARM section");
            Check(!ExplainArm.Candidate(ordinary, "// /providers/Microsoft.Example/widgets/\nold()", "// /providers/Microsoft.Example/widgets/\nnew()"),
                "Unchanged ARM reference classified an arbitrary Go edit");
        });
        await test("Explain ARM folder entries cannot require impossible records or block completed output", () =>
        {
            var folders = new[] {
                new ExplainChange(0, "/examples.bicep", null, "add", true),
                new ExplainChange(1, "/examples", "/old-examples.bicep", "rename", true),
            };
            var arm = new ExplainArm();
            Check(arm.Complete("Folder-only changes; no textual ARM deployment change inspected.", folders) ==
                "Folder-only changes; no textual ARM deployment change inspected.",
                "An unread folder entry required an impossible ARM record");
            foreach (var folder in folders)
                Check(!ExplainArm.Candidate(folder, "", """{"$schema":"https://schema.management.azure.com/schemas/example"}"""),
                    "Folder paths or supplied ARM-looking text made a folder a record candidate");
            return Task.CompletedTask;
        });
        await test("Explain ARM added deleted renamed and unavailable evidence retain honest meaningful sides", async () =>
        {
            foreach (var kind in new[] { "add", "delete", "rename, edit" })
            {
                var change = new ExplainChange(0, "/deploy.bicep", kind.StartsWith("rename") ? "/old.bicep" : null, kind, false);
                var arm = new ExplainArm();
                arm.Observe(change, kind == "add" ? "" : bicepBefore, kind == "delete" ? "" : bicepAfter);
                arm.Record(0, kind == "add" ? "absent" : "source", bicepBefore, kind == "delete" ? "absent" : "source", bicepAfter,
                    "Changes the account deployment.", "Deletion or access changes can affect consumers.", "Account added, removed or updated.");
                var output = arm.Complete("Synthetic.", [change]);
                Check(output.Contains(kind == "add" ? bicepAfter : bicepBefore) && output.Contains(kind == "rename, edit" ? "Changed lines" : "Not present"),
                    "Added/deleted/renamed resource lost meaningful source or absence");
                Check(output.Contains("```diff") && output.Contains(kind == "add" ? "+resource account" : kind == "delete" ? "-resource account" : "-    allowBlobPublicAccess"),
                    "Added/deleted/modified resource lacks actual changed-line highlighting");
            }
            foreach (var pair in new (string? Before, string? After)[] { (null, bicepAfter), (bicepBefore, null), (new string('x', 80_001), bicepAfter) })
            {
                using var f = new Fixture();
                ArmFixture(f, "/deploy.bicep", pair.Before, pair.After);
                using var context = await f.Capture();
                Check((await context.ReadChangeAsync(0, default)).Contains("No complete text diff"), "Missing/oversized evidence wasn't reported");
                var beforeMissing = pair.Before is null || pair.Before.Length > 80_000;
                await context.ReadFileAsync("/deploy.bicep", beforeMissing ? "after" : "before", 1, default);
                context.Arm.Record(0, beforeMissing ? "unavailable" : "source", beforeMissing ? "Before source missing or beyond the size limit." : bicepBefore,
                    pair.After is null ? "unavailable" : "source", pair.After is null ? "After source missing at pinned revision." : bicepAfter,
                    "The visible side sets public blob access.", "Cannot establish the complete deployment change.", "Comparison limited by missing source.");
                var limited = context.Arm.Complete("Limited evidence.", context.Changes);
                Check(limited.Contains("Bicep example unavailable") && limited.Contains(beforeMissing ? bicepAfter : bicepBefore) &&
                    context.Coverage().Length > 0, "Missing source silently invented");
            }
        });
        await test("Explain ARM grounding excludes unseen diff tails and unchanged source until targeted reads", async () =>
        {
            using var f = new Fixture();
            var padding = string.Join("\n", Enumerable.Repeat("// " + new string('x', 300), 100)) + "\n";
            ArmFixture(f, "/deploy.bicep", padding + bicepBefore, padding.Replace('x', 'y') + bicepAfter);
            using var context = await f.Capture();
            Check((await context.ReadChangeAsync(0, default)).Contains("[DIFF TRUNCATED]"), "Fixture did not truncate the source tool response");
            foreach (var kind in new[] { "source", "illustrative" })
                await Throws<ArgumentException>(() => Task.FromResult(context.Arm.Record(0, kind, bicepBefore, kind, bicepAfter,
                    "Benefit", "Risk", "Difference")));
            await context.ReadFileAsync("/deploy.bicep", "before", 101, default);
            await context.ReadFileAsync("/deploy.bicep", "after", 101, default);
            context.Arm.Record(0, "source", bicepBefore, "source", bicepAfter, "Benefit", "Risk", "Difference");
            Check(context.Arm.Complete("Read targeted source.", context.Changes).Contains("Exact source excerpt"),
                "Explicitly delivered source windows could not ground exact examples");

            using var hidden = new Fixture();
            var gap = string.Join("\n", Enumerable.Repeat("// unchanged padding", 20));
            ArmFixture(hidden, "/deploy.bicep", "// old\n" + gap + "\n" + bicepBefore, "// new\n" + gap + "\n" + bicepBefore);
            using var hiddenContext = await hidden.Capture();
            Check(!(await hiddenContext.ReadChangeAsync(0, default)).Contains("Microsoft.Storage"), "Unchanged resource was exposed in the diff");
            foreach (var kind in new[] { "source", "illustrative" })
                await Throws<ArgumentException>(() => Task.FromResult(hiddenContext.Arm.Record(0, kind, bicepBefore, "unavailable",
                    "Not inspected.", "Benefit", "Risk", "Difference")));
        });
        await test("Explain ARM rejects fabricated source derived contracts and fence escapes", async () =>
        {
            var arm = new ExplainArm();
            var change = new ExplainChange(0, "/deploy.bicep", null, "edit", false);
            arm.Observe(change, bicepBefore, bicepAfter);
            await Throws<ArgumentException>(() => Task.FromResult(arm.Record(0, "source", bicepAfter, "source", bicepAfter, "Benefit", "Risk", "Difference")));
            await Throws<ArgumentException>(() => Task.FromResult(arm.Record(0, "absent", "", "source", bicepAfter, "Benefit", "Risk", "Difference")));
            var contract = new ExplainChange(1, "/api/resource.go", null, "edit", false);
            var before = "// /providers/Microsoft.Storage/storageAccounts/ 2023-05-01\nname sample properties allowBlobPublicAccess true";
            var after = before.Replace("true", "false");
            arm.Observe(contract, before, after);
            arm.Record(1, "illustrative", bicepBefore, "illustrative", bicepAfter, "Benefit", "Compatibility uncertain", "Access changes");
            Check(arm.Complete("Synthetic.", [contract]).Contains("Illustrative Bicep derived"), "Contract-derived Bicep mislabeled exact source");
            foreach (var fabricated in new[] { bicepAfter.Replace("2023-05-01", "2099-01-01"), bicepAfter.Replace("allowBlobPublicAccess", "imaginaryFlag"),
                bicepAfter.Replace("'sample'", "'invented'"), bicepAfter.Replace("Microsoft.Storage", "Microsoft.Invented"),
                bicepAfter.Replace("properties: {", "properties: { imaginaryFlag: true,"),
                bicepAfter.Replace("properties: {", "properties: { 'imaginaryFlag': true,") })
                await Throws<ArgumentException>(() => Task.FromResult(arm.Record(1, "illustrative", bicepBefore, "illustrative", fabricated, "Benefit", "Risk", "Difference")));
            var fenced = bicepBefore + "\n// ```\n// <script>hostile()</script>";
            arm.Observe(change, fenced, bicepAfter);
            arm.Record(0, "source", fenced, "source", bicepAfter, "Benefit", "Risk", "Difference");
            Check(arm.Complete("Synthetic.", [change]).Contains("````bicep"), "Source backticks escaped the host code fence");
            Check(ExplainArm.Candidate(contract, "", "// /providers/Microsoft.Example/widgets/") &&
                ExplainArm.Candidate(new(2, "/deployment.json", null, "edit", false), "", "{\"$schema\":\"https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#\"}") &&
                !ExplainArm.Candidate(new(3, "/settings.json", null, "edit", false), "{}", "{\"enabled\":true}"), "ARM content detection is suffix-only or overbroad");
        });
        await test("Explain ARM derives API examples from inspected same-side context without borrowing target or opposite revisions", async () =>
        {
            var change = new ExplainChange(0, "/properties.go", null, "edit", false);
            var arm = new ExplainArm();
            arm.Observe(change, "allowBlobPublicAccess true", "allowBlobPublicAccess false");
            const string metadata = "Microsoft.Storage/storageAccounts 2023-05-01 name sample properties";
            arm.ObserveFile("/resource.json", "target", 1, metadata);
            await Throws<ArgumentException>(() => Task.FromResult(arm.Record(0, "illustrative", bicepBefore, "illustrative", bicepAfter,
                "Restricts public access.", "Consumers may fail.", "true to false")));
            arm.ObserveFile("/resource.json", "before", 1, metadata);
            await Throws<ArgumentException>(() => Task.FromResult(arm.Record(0, "illustrative", bicepBefore, "illustrative", bicepAfter,
                "Restricts public access.", "Consumers may fail.", "true to false")));
            arm.ObserveFile("/resource.json", "after", 1, metadata);
            arm.Record(0, "illustrative", bicepBefore, "illustrative", bicepAfter,
                "Restricts public access.", "Consumers may fail.", "true to false");
            Check(arm.Complete("The ARM API changes access.", [change]).Contains("Illustrative Bicep derived"), "Inspected same-side metadata cannot ground the API example");
            await Throws<ArgumentException>(() => Task.FromResult(arm.Record(0, "illustrative", bicepBefore, "illustrative",
                bicepAfter.Replace("'sample'", "'sam'"), "Benefit", "Risk", "Difference")));
        });
        await test("Explain runtime selects the packaged process architecture rather than the OS or another payload", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "ado-explain-runtime-" + Guid.NewGuid().ToString("N"));
            try
            {
                foreach (var (architecture, rid) in new[] { (Architecture.X64, "win-x64"), (Architecture.Arm64, "win-arm64") })
                {
                    var native = Path.Combine(directory, "runtimes", rid, "native");
                    Directory.CreateDirectory(native);
                    foreach (var name in new[] { "copilot-runtime.exe", "runtime.node", "copilot_runtime.dll" })
                        File.WriteAllText(Path.Combine(native, name), "offline fixture");
                    var options = ExplainModel.ClientOptions("isolated-home", directory, architecture);
                    Check(options.Connection is StdioRuntimeConnection connection &&
                        connection.Path == Path.Combine(native, "copilot-runtime.exe"), "SDK default OS-based resolution was not overridden");
                    Check(options.BaseDirectory == "isolated-home" && options.WorkingDirectory == "isolated-home" &&
                        options.UseLoggedInUser == true && options.LogLevel == CopilotLogLevel.Error, "Runtime selection changed isolation/auth options");
                    Check(options.Environment is not null && !options.Environment.ContainsKey("COPILOT_CLI_PATH") &&
                        !options.Environment.ContainsKey("NODE_OPTIONS"), "Ambient runtime overrides inherited");
                }
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            return Task.CompletedTask;
        });
        await test("Explain runtime rejects missing empty or incomplete matching payload without architecture or PATH fallback", async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "ado-explain-runtime-" + Guid.NewGuid().ToString("N"));
            try
            {
                var native = Path.Combine(directory, "runtimes", "win-x64", "native");
                Directory.CreateDirectory(native);
                var names = new[] { "copilot-runtime.exe", "runtime.node", "copilot_runtime.dll" };
                foreach (var name in names) File.WriteAllText(Path.Combine(native, name), "offline fixture");
                await Throws<InvalidOperationException>(() => Task.FromResult(ExplainModel.ClientOptions("offline", directory, Architecture.Arm64)));
                foreach (var name in names)
                {
                    var path = Path.Combine(native, name);
                    File.Delete(path);
                    await Throws<InvalidOperationException>(() => Task.FromResult(ExplainModel.ClientOptions("offline", directory, Architecture.X64)));
                    File.WriteAllText(path, "");
                    await Throws<InvalidOperationException>(() => Task.FromResult(ExplainModel.ClientOptions("offline", directory, Architecture.X64)));
                    File.WriteAllText(path, "offline fixture");
                }
                await Throws<PlatformNotSupportedException>(() => Task.FromResult(ExplainModel.ClientOptions("offline", directory, Architecture.X86)));
            }
            finally { Directory.Delete(directory, recursive: true); }
        });
        await test("Explain runtime options select real matching native binaries from build or publish output", () =>
        {
            var directory = Environment.GetEnvironmentVariable("ADOPRVIEW_TEST_PUBLISH_DIRECTORY") ?? AppContext.BaseDirectory;
            var options = ExplainModel.ClientOptions("offline-unused-home", directory, RuntimeInformation.ProcessArchitecture);
            Check(options.Connection is StdioRuntimeConnection, "Explain no longer uses the explicit stdio runtime");
            var connection = (StdioRuntimeConnection)options.Connection!;
            var native = Path.GetDirectoryName(connection.Path)!;
            var machine = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? Machine.Amd64 : Machine.Arm64;
            foreach (var name in new[] { "copilot-runtime.exe", "runtime.node", "copilot_runtime.dll" })
            {
                using var stream = File.OpenRead(Path.Combine(native, name));
                using var pe = new PEReader(stream);
                Check(pe.PEHeaders.CoffHeader.Machine == machine, "Selected native binary does not match the process architecture: " + name);
            }
            using var client = new CopilotClient(options);
            Console.WriteLine($"Runtime payload verified: process={RuntimeInformation.ProcessArchitecture}, OS={RuntimeInformation.OSArchitecture}, path={connection.Path}");
            return Task.CompletedTask;
        });
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
            var requests = f.Urls.Count;
            await context.ReadFileAsync("/RetryPolicy.cs", "target", 1, default);
            Check(f.Urls.Skip(requests).Where(u => u.Contains("/items?")).All(u => u.Contains(Fixture.Target)), "Target context was read from the source or main branch");
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
        await test("Explain records truncated diff evidence without claiming complete change coverage", async () =>
        {
            using var f = new Fixture();
            using var context = await f.Capture();
            f.AdditionalResponse = request =>
            {
                if (!request.RequestUri!.AbsolutePath.EndsWith("/items")) return null;
                var before = request.RequestUri.Query.Contains(Fixture.Base);
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
                {
                    path = "/RetryPolicy.cs", commitId = before ? Fixture.Base : Fixture.Source, gitObjectType = "blob",
                    contentMetadata = new { isBinary = false },
                    content = request.RequestUri.Query.Contains("includeContent=true")
                        ? string.Join('\n', Enumerable.Range(0, 600).Select(i => (before ? "before" : "after") + new string('x', 40) + i)) : null,
                })) };
            };
            var diff = await context.ReadChangeAsync(0, default);
            Check(diff.EndsWith("[DIFF TRUNCATED]") && context.Evidence().Any(e => e.StartsWith("Diff: ") && e.Contains("Partial inspection")),
                "Successful truncated diff was rejected as no evidence");
            Check(context.Coverage().Any(c => c.Contains("truncated")) && context.Coverage().Any(c => c.Contains("not had a complete")),
                "Partial diff was presented as fully inspected");
            await context.VerifyAsync(default);
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
        await test("Explain pins a refreshed target preview independently of the source iteration target and rejects later target drift", async () =>
        {
            using var f = new Fixture { TargetPreview = Fixture.Base };
            using var context = await f.Capture();
            Check(context.Revision.TargetCommit == Fixture.Base && context.Revision.SourceCommit == Fixture.Source, "Target preview confused with iteration target or source");
            await context.VerifyAsync(default);
            f.TargetPreview = Fixture.Target;
            await Throws<InvalidOperationException>(() => context.VerifyAsync(default));
        });
        await test("Explain pins live branch heads and rejects branch drift even when merge previews are stale", async () =>
        {
            using var f = new Fixture { TargetPreview = Fixture.Target, TargetHead = Fixture.Base };
            using var context = await f.Capture();
            Check(context.Revision.TargetCommit == Fixture.Base, "Target was pinned from the stale merge preview");
            f.TargetHead = Fixture.Target;
            await Throws<InvalidOperationException>(() => context.VerifyAsync(default));
            f.TargetHead = Fixture.Base; f.SourceHead = Fixture.Target;
            await Throws<InvalidOperationException>(() => context.VerifyAsync(default));
            f.SourceHead = Fixture.Source; f.TargetHead = "";
            await Throws<InvalidDataException>(() => context.VerifyAsync(default));
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
        await test("Explain preserves deep multi-section output instead of compressing it into a PR description", async () =>
        {
            using var f = new Fixture();
            var answer = "## What improves\n" + string.Join(" ", Enumerable.Repeat("Evidence-backed before/after behavior.", 90)) +
                "\n## Architecture\nThe integration-test harness owns these retries, not the shipped runtime.\n## Limits\nTests were read, not run.";
            await using var service = new ExplainService((_, ct) => f.Capture(ct), new Model((_, _, _) => Task.FromResult(answer)));
            service.Start(f.Pr.Key);
            var result = await Finish(service);
            Check(result.Status == "completed" && result.Output == answer, "Deep explanation was rejected or compressed");
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
            Check(cancelled.Status == "cancelled" && fake.Cancelled && cancelled.Output == "partial" && service.Artifacts.Length == 0,
                "Cancellation not acknowledged/partial text lost, or incomplete Explain became an attachable artifact");
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
                Check(failed.Status == "failed" && failed.Message.Contains("ADO denied") && service.Artifacts.Length == 0,
                    "Capture failure hidden or failed Explain became an attachable artifact");
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

    internal static void ArmFixture(Fixture fixture, string path, string? before, string? after, string kind = "edit")
    {
        fixture.AdditionalResponse = request =>
        {
            Check(request.Method == HttpMethod.Get, "ARM explanation attempted an ADO write");
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.EndsWith("/changes"))
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
                {
                    changeEntries = new[] { new { item = new { path }, changeType = kind } }, nextSkip = 0,
                })) };
            if (!uri.AbsolutePath.EndsWith("/items")) return null;
            var isBefore = uri.Query.Contains(Fixture.Base);
            var text = isBefore ? before : after;
            if (text is null) return new(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            {
                path, commitId = isBefore ? Fixture.Base : Fixture.Source, gitObjectType = "blob",
                contentMetadata = new { isBinary = false }, content = uri.Query.Contains("includeContent=true") ? text : null,
            })) };
        };
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
        public Func<HttpRequestMessage, HttpResponseMessage?>? AdditionalResponse;
        public string? Mismatch, OriginalPath, ContentOverride, TargetPreview, SourceHead, TargetHead;
        public string Kind = "edit";
        public string FreshStatus = "active";
        public bool FreshDraft = true;
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
            if (AdditionalResponse?.Invoke(request) is { } handled) return handled;
            Check(request.Method == HttpMethod.Get, "Explanation attempted an ADO write");
            var uri = request.RequestUri!;
            Check(uri.Host == "dev.azure.com" && uri.AbsolutePath.StartsWith($"/offline/{ProjectId}/_apis/git/repositories/{RepoId}/", StringComparison.Ordinal), "Explanation escaped repository scope");
            var url = uri.ToString(); Urls.Add(url);
            object result;
            var source = MovedSource ? Target : Source;
            var iteration = new { id = NewIteration ? 5 : 4, sourceRefCommit = new { commitId = source }, commonRefCommit = new { commitId = MovedBase ? Target : Base }, targetRefCommit = new { commitId = Target } };
            if (uri.AbsolutePath.EndsWith("/pullrequests/123"))
                result = new { pullRequestId = Mismatch == "pr" ? 999 : 123, title = Pr.Title, status = FreshStatus, isDraft = FreshDraft,
                    sourceRefName = "refs/heads/retry", targetRefName = Retargeted ? "refs/heads/other" : "refs/heads/main",
                    repository = new { id = Mismatch == "repo" ? ProjectId : RepoId, project = new { id = Mismatch == "project" ? RepoId : ProjectId } },
                    forkSource = Mismatch == "fork" ? new { repository = new { id = ProjectId } } : null,
                    lastMergeSourceCommit = new { commitId = source }, lastMergeTargetCommit = TargetPreview is null ? null : new { commitId = TargetPreview } };
            else if (uri.AbsolutePath.EndsWith("/iterations")) result = new { value = new[] { iteration } };
            else if (uri.AbsolutePath.EndsWith("/refs"))
            {
                var name = "refs/" + Uri.UnescapeDataString(uri.Query.TrimStart('?').Split('&').Single(p => p.StartsWith("filter="))["filter=".Length..]);
                result = new { value = new[] { new { name, objectId = name == "refs/heads/retry" ? SourceHead ?? source : TargetHead ?? TargetPreview ?? Target } } };
            }
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
