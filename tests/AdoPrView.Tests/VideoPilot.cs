using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AdoPrView;

// Explicit opt-in integration harness. Only the ADO GET transport is synthetic:
// source/diffs are authentic pinned public Git objects; SDK/model, voice and render are real.
static class VideoPilot
{
    const string Source = "5601cd170b1a7516442bb228a4151459bc07fbf0";
    const string Base = "8b935e6d44487591a44e36f7d0ead0e4575d9435";
    const string RepoId = "00000000-0000-4000-8000-000000000019";
    const string ProjectId = "00000000-0000-4000-8000-000000000001";

    internal static async Task DiscoverAsync(string resource, string root, string voice)
    {
        using var speech = new AzureSpeech();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var subscription = AzureSpeech.Resource(resource).Groups[1].Value;
        var subscriptions = await speech.SubscriptionsAsync(timeout.Token);
        if (!subscriptions.Any(s => s.Id.Equals(subscription, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The selected resource's subscription was not discoverable through az.");
        var candidates = await speech.FindAsync(subscription, timeout.Token);
        var candidate = candidates.SingleOrDefault(c => c.ResourceId.Equals(resource, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Azure discovery did not find the selected account.");
        if (!candidate.Compatible || !candidate.Voices.Contains(voice))
            throw new InvalidOperationException("The selected account/voice was not compatible: " + candidate.Message);
        await speech.SelectAsync(resource, voice, timeout.Token, testSynthesis: true);
        var evidence = JsonSerializer.Serialize(new { subscriptions = subscriptions.Length, candidates = candidates.Length,
            selectedResourceFound = true, voice, synthesisVerified = true, discovery = "Real read-only Azure CLI and Speech requests; no keys, provisioning or role changes." }, Bridge.Json);
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "discovery-result.json"), evidence, timeout.Token);
        Console.WriteLine(evidence);
    }

    internal static async Task SampleAsync(string resource, string root, string voice)
    {
        Directory.CreateDirectory(root);
        using var speech = new AzureSpeech();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var watch = Stopwatch.StartNew();
        var connection = await speech.SelectAsync(resource, voice, timeout.Token);
        const string text = "Your code is ready for its close-up. We'll explain what changed, why it matters, and let the cats handle the dramatic reactions.";
        var audio = await speech.SynthesizeAsync(connection, text, timeout.Token);
        VideoRenderer.WriteWave(Path.Combine(root, "azure-speech-sample.wav"), audio.Samples, audio.SampleRate);
        var result = new { voice, elapsedSeconds = watch.Elapsed.TotalSeconds, sampleRate = audio.SampleRate,
            durationSeconds = (double)audio.Samples.Length / audio.SampleRate,
            rms = Math.Sqrt(audio.Samples.Average(s => (double)s * s)), text,
            audition = "Actual Azure audio synthesized; measured and saved, not human-auditioned by the agent." };
        await File.WriteAllTextAsync(Path.Combine(root, "sample-result.json"), JsonSerializer.Serialize(result, Bridge.Json));
        Console.WriteLine(JsonSerializer.Serialize(result, Bridge.Json));
    }

    internal static async Task CheckModelAsync(string root)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var runtime = new ExplainModel(Path.GetFullPath(root));
        var client = await runtime.ClientAsync(timeout.Token);
        var models = await client.ListModelsAsync(timeout.Token);
        if (!models.Any(m => m.Id == VideoStory.Model))
            throw new InvalidOperationException("The real Copilot runtime does not advertise " + VideoStory.Model);
        Console.WriteLine("Actual authenticated bundled Copilot runtime advertises " + VideoStory.Model + ". No PR content was read or sent.");
    }

    internal static async Task<VideoState> RunAsync(string repoRoot, string artifactsRoot, IVideoVoice voice, CancellationToken ct)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        artifactsRoot = Path.GetFullPath(artifactsRoot);
        var remote = (await Git(repoRoot, ct, "remote", "get-url", "origin")).Trim();
        if (remote.TrimEnd('/').Replace(".git", "", StringComparison.Ordinal) is not
            ("https://github.com/alanmeadows/adoprview" or "git@github.com:alanmeadows/adoprview"))
            throw new InvalidOperationException("The video pilot only permits the public alanmeadows/adoprview repository.");
        Directory.CreateDirectory(artifactsRoot);
        var changes = (await Git(repoRoot, ct, "diff", "--name-status", Base, Source)).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select((line, index) =>
            {
                var fields = line.TrimEnd('\r').Split('\t');
                if (fields.Length != 2 || fields[0] is not ("M" or "A" or "D")) throw new InvalidDataException("Unexpected public pilot change.");
                return new { item = new { path = "/" + fields[1] }, changeType = fields[0] switch { "A" => "add", "D" => "delete", _ => "edit" } };
            }).ToArray();
        var requests = new List<string>();
        using var http = new HttpClient(new Transport(async (request, token) =>
        {
            if (request.Method != HttpMethod.Get) throw new InvalidOperationException("The pilot forbids all ADO writes.");
            var uri = request.RequestUri!;
            if (uri.Host != "dev.azure.com" || !uri.AbsolutePath.StartsWith($"/public-pilot/{ProjectId}/_apis/git/repositories/{RepoId}/", StringComparison.Ordinal))
                throw new InvalidOperationException("The pilot attempted to escape its synthetic repository transport.");
            lock (requests) requests.Add(uri.ToString());
            object result;
            if (uri.AbsolutePath.EndsWith("/pullrequests/19", StringComparison.Ordinal))
                result = new { pullRequestId = 19, title = "Keep human actions visible during merge rechecks", status = "active", isDraft = false,
                    sourceRefName = "refs/heads/public-pr-19-fixture", targetRefName = "refs/heads/main",
                    repository = new { id = RepoId, project = new { id = ProjectId } }, lastMergeSourceCommit = new { commitId = Source } };
            else if (uri.AbsolutePath.EndsWith("/iterations", StringComparison.Ordinal))
                result = new { value = new[] { new { id = 1, sourceRefCommit = new { commitId = Source }, commonRefCommit = new { commitId = Base } } } };
            else if (uri.AbsolutePath.EndsWith("/changes", StringComparison.Ordinal))
                result = new { changeEntries = changes, nextSkip = 0 };
            else if (uri.AbsolutePath.EndsWith("/items", StringComparison.Ordinal))
            {
                var query = uri.Query.TrimStart('?').Split('&').Select(s => s.Split('=', 2))
                    .ToDictionary(p => p[0], p => p.Length == 2 ? Uri.UnescapeDataString(p[1]) : "");
                var commit = query["versionDescriptor.version"];
                if (commit is not (Base or Source) || query["versionDescriptor.versionType"] != "commit")
                    throw new InvalidOperationException("The pilot forbids unpinned revisions.");
                if (query.TryGetValue("scopePath", out var scope))
                {
                    ExplainContext.ValidatePath(scope);
                    var tree = commit + (scope == "/" ? "" : ":" + scope.TrimStart('/'));
                    var entries = (await Git(repoRoot, token, "ls-tree", "-z", tree)).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                    result = new { value = entries.Select(entry =>
                    {
                        var pieces = entry.Split('\t', 2);
                        return new { path = scope.TrimEnd('/') + "/" + pieces[1], commitId = commit, isFolder = pieces[0].Split(' ')[1] == "tree" };
                    }).ToArray() };
                }
                else
                {
                    var path = ExplainContext.ValidatePath(query["path"]);
                    var content = await Git(repoRoot, token, "show", commit + ":" + path.TrimStart('/'));
                    result = new { path, commitId = commit, gitObjectType = "blob", contentMetadata = new { isBinary = content.Contains('\0') },
                        content = query.ContainsKey("includeContent") ? content : null };
                }
            }
            else throw new InvalidOperationException("Unexpected synthetic pilot endpoint.");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(result), Encoding.UTF8, "application/json") };
        }));
        var ado = new AdoClient(http, _ => Task.FromResult(new AuthenticationHeaderValue("Bearer", "public-fixture-no-credential")));
        var pr = new PrView
        {
            Key = RepoId + ":19", Id = 19, Title = "Public PR 19 pilot", Repo = "adoprview", Org = "public-pilot",
            Project = "Public", ProjectId = ProjectId, RepoId = RepoId, Author = new("Public pilot", null, null), IsActive = true,
            Url = "https://github.com/alanmeadows/adoprview/pull/19",
        };
        var artifacts = new VideoArtifacts(artifactsRoot);
        var model = new VideoModel(Path.Combine(artifactsRoot, "copilot-runtime"));
        await using var service = new VideoService((_, token) => ExplainContext.CaptureAsync(ado, pr, token, requireActive: true),
            _ => { }, artifacts, new VideoRenderer(voice), model);
        var done = new TaskCompletionSource<VideoState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watch = Stopwatch.StartNew();
        var states = new List<object>();
        service.Changed += state =>
        {
            Console.WriteLine($"{watch.Elapsed:mm\\:ss} {state.Status}: {state.Message}");
            states.Add(new { elapsedSeconds = watch.Elapsed.TotalSeconds, state.Status, state.Message });
            if (!VideoService.Active(state.Status)) done.TrySetResult(state);
        };
        var start = service.Start(pr.Key);
        using var cancel = ct.Register(() => _ = service.CancelAsync(pr.Key, start.RequestId));
        var resultState = await done.Task.WaitAsync(ct);
        var evidence = new { publicPullRequest = "https://github.com/alanmeadows/adoprview/pull/19", source = Source, @base = Base,
            elapsedSeconds = watch.Elapsed.TotalSeconds, synthetic = "ADO GET transport and dashboard PR identity only",
            actual = "Pinned public Git object reads through production context tools; native Copilot SDK Opus 5.5; selected voice; PNG scenes; Windows H264/AAC render and decoded-frame validation",
            state = resultState, states, sourceRequests = requests };
        await File.WriteAllTextAsync(Path.Combine(artifactsRoot, "pilot-result.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions(Bridge.Json) { WriteIndented = true }), ct);
        Console.WriteLine(JsonSerializer.Serialize(resultState, Bridge.Json));
        return resultState;
    }

    static async Task<string> Git(string root, CancellationToken ct, params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "--no-optional-locks", "-c", "core.fsmonitor=false", "--no-pager" }.Concat(args))
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("Could not start read-only public source fixture.");
        using var cancel = ct.Register(() => { if (!process.HasExited) process.Kill(entireProcessTree: true); });
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var errors = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var text = await output;
        if (process.ExitCode != 0) throw new IOException("Public Git fixture read failed: " + await errors);
        if (text.Length > 2_000_000) throw new InvalidDataException("Public fixture response exceeds the source-read bound.");
        return text;
    }

    sealed class Transport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
