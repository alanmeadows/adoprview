using System.Text.Json;
using AdoPrView;

static class VideoTests
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static async Task Throws<T>(Func<Task> run) where T : Exception
    {
        try { await run(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    const string Spoken = "The dashboard shows developers what needs their attention. This change keeps useful human actions visible while the merge preview updates.";
    static readonly VideoCatShot NoCat = new("", "", "", "", "");
    static VideoScene Scene(string layout, VideoCatShot cat, params VideoBeat[] beats) => new(layout, "Merge lanes", Spoken, beats, cat, "drift");

    internal static VideoStory Story() => new("A clearer dashboard", [new("yawn", "The PR waiting on a preview")],
    [
        Scene("hook", new("yawn", "portrait", "right", "enter", "Still waiting..."), new VideoBeat("Stuck behind a preview?", "dashboard", "alert")),
        Scene("term", NoCat, new("Merge preview", "merge preview", "plain"), new("A recheck, not a blocker", "updates", "new")),
        Scene("contrast", new("yawn", "portrait", "before", "peek", ""), new("Actions hidden", "dashboard", "old"), new("Actions visible", "human actions", "new")),
        Scene("journey", new("yawn", "face", "travel", "pop", "My turn!"), new("Open PR", "dashboard", "plain"),
            new("Human action", "human actions", "new"), new("Preview rechecks", "merge preview", "plain")),
        Scene("list", NoCat, new("No hidden work", "human actions", "new"), new("Preview still runs", "merge preview", "plain")),
        Scene("hook", NoCat, new VideoBeat("Act now", "human actions", "new")),
    ]);

    static VideoStory Change(VideoStory story, int index, Func<VideoScene, VideoScene> change) =>
        story with { Scenes = story.Scenes.Select((s, i) => i == index ? change(s) : s).ToArray() };

    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("Video context rejects inactive hidden ignored and freshly drafted PRs before source inspection", async () =>
        {
            using var f = new ExplainTests.Fixture();
            f.Pr.IsActive = false;
            await Throws<InvalidOperationException>(() => f.Service.GetVideoContextAsync(f.Pr.Key, default));
            f.Pr.IsActive = true; f.FreshStatus = "completed";
            await Throws<InvalidOperationException>(() => f.Service.GetVideoContextAsync(f.Pr.Key, default));
            f.FreshStatus = "active"; f.Pr.IsDraft = false; f.Store.Current.IgnoreDrafts = true;
            await Throws<InvalidOperationException>(() => f.Service.GetVideoContextAsync(f.Pr.Key, default));
            f.FreshDraft = false;
            using var context = await f.Service.GetVideoContextAsync(f.Pr.Key, default);
            f.FreshStatus = "completed";
            await Throws<InvalidOperationException>(() => context.VerifyAsync(default));
            f.FreshStatus = "active"; f.FreshDraft = true; f.Store.Current.IgnoreDrafts = false;
            using var newlyIgnored = await f.Service.GetVideoContextAsync(f.Pr.Key, default);
            f.Store.Current.IgnoreDrafts = true;
            await Throws<InvalidOperationException>(() => newlyIgnored.VerifyAsync(default));
            Check(!f.Urls.Any(u => u.Contains("/items?")), "Ineligible video read source content");
        });
        await test("Video storyboard accepts cast cats cued beats and rejects URLs unsynced cues stray cats and copied captions", async () =>
        {
            var good = Story();
            good.Validate(["yawn", "rugby"]);
            Check(VideoStory.Parse(JsonSerializer.Serialize(good, Bridge.Json), ["yawn"]).Scenes.Length == 6, "Valid storyboard lost");
            foreach (var bad in new[]
            {
                Change(good, 0, s => s with { Beats = [new("Stuck", "never spoken words", "alert")] }),
                Change(good, 2, s => s with { Beats = [s.Beats[1], s.Beats[0]] }),
                Change(good, 0, s => s with { Cat = s.Cat with { CatId = "rugby" } }),
                Change(good, 3, s => s with { Cat = s.Cat with { Shot = "portrait" } }),
                Change(good, 2, s => s with { Cat = s.Cat with { Place = "left" } }),
                Change(good, 0, s => s with { Cat = s.Cat with { Bubble = "see https://evil.invalid" } }),
                Change(good, 1, s => s with { Cat = NoCat with { Shot = "portrait" } }),
                Change(good, 3, s => s with { Cat = NoCat }),
                Change(good, 4, s => s with { Beats = [new("keeps useful human actions visible while", "human actions", "new"), s.Beats[1]] }),
                Change(good, 4, s => s with { Beats = [s.Beats[0], new("Merge lanes", "merge preview", "plain")] }),
                Change(good, 0, s => s with { Layout = "shell" }),
                Change(good, 1, s => s with { Beats = [s.Beats[0]] }),
                good with { Cast = [new("lion", "An invented asset")] },
                good with { Scenes = good.Scenes.Select(s => s with { Layout = "list", Cat = s.Cat with { Place = s.Cat.CatId == "" ? "" : "left", Shot = s.Cat.CatId == "" ? "" : "portrait" },
                    Beats = [s.Beats[0], s.Beats[0]] }).ToArray() },
            })
                await Throws<InvalidDataException>(() => Task.Run(() => bad.Validate(["yawn", "rugby"])));
            await Throws<JsonException>(() => Task.FromResult(VideoStory.Parse("""{"title":"x","cast":[],"scenes":[],"command":"bad"}""", ["yawn"])));
            Check(VideoRenderer.Captions("One two three four five six seven eight nine ten eleven twelve.")
                .All(s => s.Split(' ').Length <= 9), "Captions are too long to read");
            Check(VideoRenderer.Captions("One two three four five six seven eight nine ten.")
                .All(s => s.Split(' ').Length >= 3), "A short orphan caption flashes past unreadably");
        });
        await test("Video scenes animate beats and cats in sync with their narration cues", async () =>
        {
            var story = Story();
            var arts = new Dictionary<string, VideoRenderer.CatArt> { ["yawn"] = VideoRenderer.CatArt.Placeholder("yawn") };
            try
            {
                foreach (var index in new[] { 2, 3 })
                {
                    var plan = VideoRenderer.ScenePlan.Create(story, index, 0, 8);
                    Check(plan.Cues.Zip(plan.Cues.Skip(1)).All(p => p.First < p.Second) && plan.Cues.All(c => c > 0 && c < 8),
                        "Beat cues are not ordered within the spoken scene");
                    var frames = new[] { 0.05, plan.Cues[^1] - 0.3, plan.Duration - 0.01 }.Select(t =>
                    {
                        using var bitmap = new System.Drawing.Bitmap(VideoStory.Width, VideoStory.Height);
                        using (var g = System.Drawing.Graphics.FromImage(bitmap)) VideoRenderer.DrawFrame(g, plan, t, arts, null);
                        using var stream = new MemoryStream();
                        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Bmp);
                        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream.ToArray()));
                    }).ToArray();
                    Check(frames.Distinct().Count() == frames.Length, $"Scene {index} is a static slide instead of animating with its cues");
                }
            }
            finally { foreach (var art in arts.Values) art.Dispose(); }
        });
        await test("Video dependency setup retries failures shares concurrent checks and reuses successful bundled dependencies", async () =>
        {
            var checks = 0;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var renderer = new VideoRenderer(new UnusedVoice(), checkMedia: async ct =>
            {
                if (Interlocked.Increment(ref checks) == 1) throw new InvalidOperationException("Synthetic missing AAC encoder");
                entered.SetResult(); await release.Task.WaitAsync(ct);
            });
            try
            {
                await renderer.PrepareDependenciesAsync(default);
                throw new Exception("Missing media dependency was treated as ready.");
            }
            catch (InvalidOperationException ex) { Check(ex.Message.Contains("AAC") && ex.Message.Contains("retry"), "Dependency failure has no actionable retry detail."); }
            var retry = renderer.PrepareDependenciesAsync(default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var concurrent = renderer.PrepareDependenciesAsync(default);
            release.SetResult();
            await Task.WhenAll(retry, concurrent);
            await renderer.PrepareDependenciesAsync(default);
            Check(checks == 2, "Successful dependencies were reinstalled/rechecked or concurrent setup was duplicated.");
        });
        await test("Video SDK selects exactly Opus 5.5 and inherits the source-only denied-permission policy", async () =>
        {
            using var f = new ExplainTests.Fixture();
            using var context = await f.Capture();
            var config = VideoModel.Config("offline-owned-job", context, default);
            Check(config.Model == "claude-opus-5.5" && config.AvailableTools!.SequenceEqual(ExplainModel.ToolNames), "Wrong model or tool inventory");
            Check(config.EnableHostGitOperations == false && config.EnableFileHooks == false &&
                config.EnableConfigDiscovery == false && config.EnableSkills == false &&
                config.SkipCustomInstructions == true && config.McpServers!.Count == 0, "Ambient capability enabled");
            Check(!JsonSerializer.Serialize(await config.OnPermissionRequest!(new(), new())).Contains("approve",
                StringComparison.OrdinalIgnoreCase), "Video approved arbitrary permissions");
        });
        await test("Video job deduplicates starts records source evidence and publishes only the host-owned completed path", async () =>
        {
            using var f = new ExplainTests.Fixture();
            var root = Temp();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var renderer = new Renderer(async (_, _, ct) => { entered.SetResult(); await release.Task.WaitAsync(ct); });
            try
            {
                var artifacts = new VideoArtifacts(root);
                await using var service = new VideoService((_, ct) => f.Capture(ct), _ => { }, artifacts, renderer, new Model());
                var done = Terminal(service);
                var state = service.Start(f.Pr.Key);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(service.Start(f.Pr.Key).RequestId == state.RequestId, "Duplicate generation was started");
                await Throws<InvalidOperationException>(() => Task.FromResult(service.Start("other-pr")));
                Check(!File.Exists(artifacts.Ready(state.RequestId)), "Partial video was published");
                release.SetResult();
                var ready = await done;
                Check(ready.Status == "completed" && ready.Path == artifacts.Ready(state.RequestId) &&
                    ready.MediaUrl == $"https://pr-video.local/{state.RequestId}.mp4" &&
                    ready.Evidence.Any(s => s.Contains("Diff:")) && ready.Evidence.Any(s => s.Contains("README")), "Ready video lost identity, path or source evidence");
                Check(!File.Exists(artifacts.Pending(state.RequestId)) && File.Exists(ready.Path), "Publication was not atomic");
            }
            finally { Directory.Delete(root, true); }
        });
        await test("Video missing media invalid media stale source and missing source-tool evidence never become Ready", async () =>
        {
            foreach (var failure in new[] { "missing", "invalid", "stale", "unread", "setup" })
            {
                using var f = new ExplainTests.Fixture();
                var root = Temp();
                try
                {
                    var artifacts = new VideoArtifacts(root);
                    var renderer = new Renderer((_, _, _) => { if (failure == "stale") f.MovedSource = true; return Task.CompletedTask; })
                        { Missing = failure == "missing", Invalid = failure == "invalid", SetupFailure = failure == "setup" };
                    await using var service = new VideoService((_, ct) => f.Capture(ct), _ => { }, artifacts, renderer,
                        new Model { ReadSource = failure != "unread" });
                    var done = Terminal(service);
                    var state = service.Start(f.Pr.Key);
                    var result = await done;
                    Check(result.Status == "failed" && result.Path is null && result.MediaUrl is null &&
                        !File.Exists(artifacts.Ready(state.RequestId)), failure + " published a playable video");
                    if (failure == "setup") Check(f.Urls.Count == 0, "Unconfigured voice caused PR content reads");
                }
                finally { Directory.Delete(root, true); }
            }
        });
        await test("Video cancellation is immediate exact-job scoped and shutdown awaits owned work without resuming successes", async () =>
        {
            using var f = new ExplainTests.Fixture();
            var root = Temp();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var renderer = new Renderer(async (_, _, ct) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); });
            try
            {
                var artifacts = new VideoArtifacts(root);
                var model = new Model();
                var service = new VideoService((_, ct) => f.Capture(ct), _ => { }, artifacts, renderer, model);
                var done = Terminal(service);
                var state = service.Start(f.Pr.Key);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Throws<InvalidOperationException>(() => service.CancelAsync(f.Pr.Key, "stale-id"));
                var cancelling = await service.CancelAsync(f.Pr.Key, state.RequestId);
                Check(cancelling.Status is "cancelling" or "cancelled", "Cancel waited on the model/render");
                await service.DisposeAsync();
                await service.DisposeAsync();
                Check((await done).Status == "cancelled" && model.Disposed && !File.Exists(artifacts.Ready(state.RequestId)), "Shutdown leaked or published cancelled work");
            }
            finally { Directory.Delete(root, true); }
        });
        await test("Video media serves exact registered MP4s with bounded seek suffix HEAD and rejection responses", async () =>
        {
            var root = Temp();
            try
            {
                var artifacts = new VideoArtifacts(root);
                var id = Guid.NewGuid().ToString("N");
                artifacts.CreateWork(id);
                var data = Enumerable.Range(0, 4000).Select(i => (byte)(i % 251)).ToArray();
                await File.WriteAllBytesAsync(artifacts.Pending(id), data);
                var ready = artifacts.Publish(id);
                foreach (var (range, start, length) in new[] { ("bytes=100-199", 100, 100), ("bytes=3900-", 3900, 100),
                    ("bytes=-50", 3950, 50), ("bytes=3980-9999", 3980, 20) })
                {
                    using var response = artifacts.Open(ready.Url, "GET", range);
                    using var buffer = new MemoryStream();
                    await response.Body!.CopyToAsync(buffer);
                    Check(response.Status == 206 && buffer.ToArray().SequenceEqual(data.Skip(start).Take(length)) &&
                        response.Headers.Contains($"Content-Length: {length}"), "Incorrect media byte range: " + range);
                }
                foreach (var range in new[] { "bytes=9999-", "bytes=200-100", "bytes=-0", "bytes=1-2,4-5", "bytes=999999999999999999999-", "bytes=-" })
                {
                    using var response = artifacts.Open(ready.Url, "GET", range);
                    Check(response.Status == 416 && response.Body is null, "Invalid range accepted");
                }
                foreach (var url in new[] { ready.Url + "?path=C:\\secret", ready.Url.Replace(id, "../" + id),
                    ready.Url.Replace(id, Guid.NewGuid().ToString("N")), ready.Url.Replace(".mp4", ".json"), "file:///" + ready.Path })
                {
                    using var response = artifacts.Open(url, "GET", null);
                    Check(response.Status == 404, "Arbitrary or unregistered media path opened");
                }
                using var head = artifacts.Open(ready.Url, "HEAD", null);
                using var post = artifacts.Open(ready.Url, "POST", null);
                Check(head.Status == 200 && head.Body is null && head.Headers.Contains("Content-Length: 4000") &&
                    post.Status == 405, "Invalid HEAD/method semantics");
            }
            finally { Directory.Delete(root, true); }
        });
    }

    static string Temp()
    {
        var path = Path.Combine(Path.GetTempPath(), "ado-video-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
    static Task<VideoState> Terminal(VideoService service)
    {
        var terminal = new TaskCompletionSource<VideoState>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Changed += s => { if (!VideoService.Active(s.Status)) terminal.TrySetResult(s); };
        return terminal.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
    sealed class UnusedVoice : IVideoVoice
    {
        public string Name => "Unused offline voice";
        public Task PrepareAsync(CancellationToken ct) => throw new Exception("Local dependency setup must not contact a voice provider.");
        public Task<VideoSpeech> SpeakAsync(string text, CancellationToken ct) => throw new Exception("Local dependency setup must not synthesize narration.");
    }
    sealed class Model : IVideoModel
    {
        public bool ReadSource = true, Disposed;
        public async Task<VideoStory> CreateAsync(ExplainContext context, string work, string catalog, string[] ids,
            Action<string> progress, CancellationToken ct)
        {
            if (ReadSource) { await context.ReadChangeAsync(0, ct); await context.ReadFileAsync("/README.md", "after", 1, ct); }
            return Story();
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    sealed class Renderer(Func<VideoStory, string, CancellationToken, Task> run) : IVideoRenderer
    {
        public bool Missing, Invalid, SetupFailure;
        public Task<VideoResources> PrepareAsync(CancellationToken ct) => SetupFailure
            ? throw new InvalidOperationException("Natural voice setup missing") : Task.FromResult(new VideoResources("offline", ["yawn"], "Offline test voice"));
        public async Task RenderAsync(VideoStory story, string work, string output, Action<string> progress, CancellationToken ct)
        {
            await run(story, work, ct);
            if (!Missing) await File.WriteAllBytesAsync(output, new byte[2048], ct);
        }
        public Task<double> ValidateAsync(string path, CancellationToken ct) => Invalid || !File.Exists(path)
            ? throw new InvalidDataException("Invalid offline media") : Task.FromResult(60.0);
    }
}
