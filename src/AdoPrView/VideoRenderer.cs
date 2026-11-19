using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Text.Json;
using System.Runtime.InteropServices;
using Windows.Media.Core;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace AdoPrView;

internal sealed record VideoSpeech(float[] Samples, int SampleRate);
internal interface IVideoVoice
{
    string Name { get; }
    Task PrepareAsync(CancellationToken ct);
    Task<VideoSpeech> SpeakAsync(string text, CancellationToken ct);
}

internal sealed record CatAsset(string Id, string File, string Description, string Author, string Source, string License, string Sha256);

internal sealed class VideoRenderer(IVideoVoice voice, string? assetDirectory = null,
    Func<CancellationToken, Task>? checkMedia = null, Action<string>? warning = null) : IVideoRenderer
{
    readonly string _assets = Path.GetFullPath(assetDirectory ?? Path.Combine(AppContext.BaseDirectory, "VideoAssets"));
    CatAsset[]? _cats;
    readonly SemaphoreSlim _setupGate = new(1, 1);
    bool _dependenciesReady;
    internal static readonly Color Ink = Color.FromArgb(22, 27, 34);
    internal static readonly Color Accent = Color.FromArgb(175, 231, 211);
    internal static readonly Color Muted = Color.FromArgb(182, 198, 210);

    public async Task<VideoResources> PrepareAsync(CancellationToken ct)
    {
        await PrepareDependenciesAsync(ct);
        await voice.PrepareAsync(ct);
        var cats = _cats!;
        return new(JsonSerializer.Serialize(cats.Select(c => new { c.Id, c.Description, framing = Framing(c.Id) }), Bridge.Json),
            cats.Select(c => c.Id).ToArray(), voice.Name);
    }

    internal async Task PrepareDependenciesAsync(CancellationToken ct)
    {
        await _setupGate.WaitAsync(ct);
        try
        {
            if (_dependenciesReady) return;
            _ = ExplainModel.ClientOptions(_assets, AppContext.BaseDirectory, RuntimeInformation.ProcessArchitecture);
            try { await PrepareAssetsAsync(ct); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            {
                throw new InvalidOperationException("Bundled real-cat catalog/photo dependency check failed: " + ex.Message +
                    " Reinstall the complete application package, then retry video setup.", ex);
            }
            try { await (checkMedia ?? CheckMediaAsync)(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException("Windows H.264/AAC media dependency check failed: " + ex.Message +
                    " On Windows N, install Microsoft's Media Feature Pack in Optional features, restart, then retry video setup. " +
                    "Other editions: repair Windows media components. FFmpeg is not required.", ex);
            }
            ct.ThrowIfCancellationRequested();
            _dependenciesReady = true;
        }
        finally { _setupGate.Release(); }
    }

    static async Task CheckMediaAsync(CancellationToken ct)
    {
        var query = new CodecQuery();
        foreach (var (kind, subtype, name) in new[]
        {
            (CodecKind.Video, CodecSubtypes.VideoFormatH264, "H.264"),
            (CodecKind.Audio, CodecSubtypes.AudioFormatAac, "AAC"),
        })
            if ((await query.FindAllAsync(kind, CodecCategory.Encoder, subtype).AsTask(ct)).Count == 0)
                throw new InvalidOperationException($"The Windows {name} encoder is unavailable.");
    }

    async Task PrepareAssetsAsync(CancellationToken ct)
    {
        var catalogPath = Path.Combine(_assets, "cats.json");
        VideoArtifacts.RequireOrdinaryPath(catalogPath);
        var text = await File.ReadAllTextAsync(catalogPath, ct);
        var cats = JsonSerializer.Deserialize<CatAsset[]>(text, VideoStory.Json)
            ?? throw new InvalidDataException("The verified real-cat catalog is missing.");
        if (cats.Length is < 2 or > 8 || cats.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != cats.Length)
            throw new InvalidDataException("The real-cat catalog must contain 2-8 unique assets.");
        foreach (var cat in cats)
        {
            if (cat.License is not ("CC0-1.0" or "Unsplash"))
                throw new InvalidDataException($"Cat photo '{cat.Id}' has unsupported license '{cat.License}'.");
            if (Path.GetFileName(cat.File) != cat.File ||
                cat.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') || cat.Source.Length > 300)
                throw new InvalidDataException("The real-cat catalog has an invalid asset.");
            var file = Path.Combine(_assets, cat.File);
            VideoArtifacts.RequireOrdinaryPath(file);
            await using var stream = File.OpenRead(file);
            var hash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, ct));
            if (!hash.Equals(cat.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A cat photo did not match its catalog hash. Reinstall the video assets.");
        }
        _cats = cats;
    }

    internal const int Fps = 15;
    const double ScenePause = 0.35, CrossFade = 0.35;

    public async Task RenderAsync(VideoStory story, string work, string output, Action<string> progress, CancellationToken ct)
    {
        var catalog = _cats ?? throw new InvalidOperationException("Video assets were not prepared.");
        story.Validate(catalog.Select(c => c.Id));
        var allSamples = new List<float>();
        var plans = new List<ScenePlan>();
        var sampleRate = 0;
        for (var index = 0; index < story.Scenes.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            var scene = story.Scenes[index];
            progress($"Generating narration for scene {index + 1} of {story.Scenes.Length}...");
            var speech = await voice.SpeakAsync(scene.Narration, ct);
            if (speech.SampleRate is < 16000 or > 48000 || speech.Samples.Length < speech.SampleRate ||
                speech.Samples.Length > speech.SampleRate * 35 || speech.Samples.Any(s => !float.IsFinite(s)) ||
                Math.Sqrt(speech.Samples.Average(s => (double)s * s)) < 0.001)
                throw new InvalidDataException("The neural voice returned missing, silent or invalid narration.");
            if (sampleRate != 0 && sampleRate != speech.SampleRate)
                throw new InvalidDataException("The neural voice changed sample rate during generation.");
            sampleRate = speech.SampleRate;
            var start = (double)allSamples.Count / sampleRate;
            allSamples.AddRange(speech.Samples);
            allSamples.AddRange(new float[(int)(sampleRate * ScenePause)]);
            plans.Add(ScenePlan.Create(story, index, start, (double)speech.Samples.Length / sampleRate));
        }
        var total = (double)allSamples.Count / sampleRate;
        if (total is < 30 or > 150)
            throw new InvalidDataException($"Narration is {total:F0} seconds; the short-video safety limit is 30-150 seconds. Retry with a more concise storyboard.");
        var wav = Path.Combine(work, "narration.wav");
        WriteWave(wav, allSamples, sampleRate);

        var frames = Path.Combine(work, "frames");
        Directory.CreateDirectory(frames);
        var failed = true;
        try
        {
            var clips = new List<(string File, int Frames)>();
            var arts = new Dictionary<string, CatArt>(StringComparer.Ordinal);
            Bitmap? last = null;
            try
            {
                foreach (var member in story.Cast)
                    arts[member.CatId] = CatArt.Load(member.CatId, Path.Combine(_assets, catalog.Single(c => c.Id == member.CatId).File));
                using var canvas = new Bitmap(VideoStory.Width, VideoStory.Height, PixelFormat.Format32bppArgb);
                var current = new byte[VideoStory.Width * VideoStory.Height * 4];
                var previous = new byte[current.Length];
                var totalFrames = (int)Math.Ceiling(total * Fps);
                using var quality = new EncoderParameters(1);
                quality.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
                var jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                foreach (var plan in plans)
                {
                    progress($"Animating scene {plan.Index + 1} of {plans.Count}...");
                    var first = (int)Math.Round(plan.Start * Fps);
                    var end = plan.Index == plans.Count - 1 ? totalFrames : (int)Math.Round((plan.Start + plan.Duration) * Fps);
                    for (var frame = first; frame < end; frame++)
                    {
                        ct.ThrowIfCancellationRequested();
                        using (var g = Graphics.FromImage(canvas))
                            DrawFrame(g, plan, (double)frame / Fps - plan.Start, arts, last);
                        var bits = canvas.LockBits(new Rectangle(0, 0, canvas.Width, canvas.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                        try { Marshal.Copy(bits.Scan0, current, 0, current.Length); }
                        finally { canvas.UnlockBits(bits); }
                        // Held poses become one longer still clip, keeping the composition small.
                        if (clips.Count > 0 && current.AsSpan().SequenceEqual(previous))
                        {
                            clips[^1] = (clips[^1].File, clips[^1].Frames + 1);
                            continue;
                        }
                        var file = Path.Combine(frames, $"frame-{frame:D5}.jpg");
                        canvas.Save(file, jpeg, quality);
                        clips.Add((file, 1));
                        (current, previous) = (previous, current);
                    }
                    canvas.Save(Path.Combine(work, $"scene-{plan.Index + 1:D2}.png"), ImageFormat.Png);
                    last?.Dispose();
                    last = (Bitmap)canvas.Clone();
                }
            }
            finally
            {
                last?.Dispose();
                foreach (var art in arts.Values) art.Dispose();
            }
            await File.WriteAllTextAsync(Path.Combine(work, "timeline.json"), JsonSerializer.Serialize(new
            {
                fps = Fps, frames = (int)Math.Ceiling(total * Fps), stillClips = clips.Count, totalSeconds = total,
                cast = story.Cast,
                scenes = plans.Select(p => new
                {
                    scene = p.Index + 1, p.Scene.Layout, startSeconds = p.Start, durationSeconds = p.Duration,
                    beatSeconds = p.Cues, captions = p.Captions.Select(c => new { c.Text, startSeconds = c.Start }),
                }),
                timing = "Beat cues and captions are approximate, weighted by text position within each measured scene narration.",
            }, Bridge.Json), ct);
            progress("Encoding the animated H.264/AAC MP4 locally...");
            var composition = new MediaComposition();
            foreach (var (file, count) in clips)
            {
                ct.ThrowIfCancellationRequested();
                var image = await StorageFile.GetFileFromPathAsync(file).AsTask(ct);
                composition.Clips.Add(await MediaClip.CreateFromImageFileAsync(image,
                    TimeSpan.FromTicks((long)Math.Round(count * (double)TimeSpan.TicksPerSecond / Fps))).AsTask(ct));
            }
            composition.BackgroundAudioTracks.Add(await BackgroundAudioTrack.CreateFromFileAsync(
                await StorageFile.GetFileFromPathAsync(wav).AsTask(ct)).AsTask(ct));
            var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(output)!).AsTask(ct);
            var target = await folder.CreateFileAsync(Path.GetFileName(output), CreationCollisionOption.FailIfExists).AsTask(ct);
            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD720p);
            profile.Video.Width = VideoStory.Width;
            profile.Video.Height = VideoStory.Height;
            profile.Video.Bitrate = 4_000_000;
            profile.Video.FrameRate.Numerator = 30;
            profile.Video.FrameRate.Denominator = 1;
            using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(ct);
            watchdog.CancelAfter(TimeSpan.FromMinutes(20));
            var result = await composition.RenderToFileAsync(target, MediaTrimmingPreference.Precise, profile).AsTask(watchdog.Token);
            if (result != TranscodeFailureReason.None)
                throw new InvalidOperationException($"Windows could not render H.264/AAC ({result}). Check Windows media features and available disk space.");
            failed = false;
        }
        finally { DeleteFrames(frames, failed); }
    }

    // A failed cleanup is always reported: to the host's warning callback, or as an IOException when no callback is
    // supplied. An earlier render failure is never masked by a cleanup error.
    void DeleteFrames(string frames, bool failed)
    {
        try { if (Directory.Exists(frames)) Directory.Delete(frames, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var message = $"Temporary video frames could not be deleted and remain in \"{frames}\" ({ex.Message}). " +
                "Close any program that may be using those images, then delete that folder manually.";
            if (warning is not null) warning(message);
            else if (!failed) throw new IOException(message, ex);
        }
    }

    public async Task<double> ValidateAsync(string path, CancellationToken ct)
    {
        VideoArtifacts.RequireOrdinaryPath(path);
        if (!File.Exists(path) || new FileInfo(path).Length < 1024)
            throw new InvalidDataException("The renderer did not produce an MP4.");
        var file = await StorageFile.GetFileFromPathAsync(path).AsTask(ct);
        var encoded = await MediaEncodingProfile.CreateFromFileAsync(file).AsTask(ct);
        var clip = await MediaClip.CreateFromFileAsync(file).AsTask(ct);
        if (encoded.Container?.Subtype != "MPEG4" || encoded.Video?.Subtype != "H264" ||
            encoded.Video.Width != VideoStory.Width || encoded.Video.Height != VideoStory.Height ||
            encoded.Audio?.Subtype != "AAC" || encoded.Audio.SampleRate < 16000 ||
            clip.OriginalDuration.TotalSeconds is < 30 or > 151)
            throw new InvalidDataException("The output is not the expected short 1280x720 H.264/AAC MP4.");
        // Decode representative actual frames, rather than accepting metadata or an MP4 extension alone.
        var decoded = new MediaComposition();
        decoded.Clips.Add(clip);
        foreach (var fraction in new[] { 0.0, 0.5, 0.95 })
        {
            using var frame = await decoded.GetThumbnailAsync(TimeSpan.FromSeconds(clip.OriginalDuration.TotalSeconds * fraction),
                320, 180, VideoFramePrecision.NearestFrame).AsTask(ct);
            if (frame.Size == 0) throw new InvalidDataException("Windows could not decode a frame from the rendered video.");
        }
        return clip.OriginalDuration.TotalSeconds;
    }

    internal static string[] Captions(string text)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var result = new List<string>();
        var chunk = new List<string>();
        foreach (var word in words)
        {
            chunk.Add(word);
            if (chunk.Count >= 9 || chunk.Count >= 4 && word.EndsWith('.'))
            { result.Add(string.Join(" ", chunk)); chunk.Clear(); }
        }
        if (chunk.Count > 0) result.Add(string.Join(" ", chunk));
        if (result.Count > 1 && result[^1].Split(' ').Length < 3)
        {
            var tail = (result[^2] + " " + result[^1]).Split(' ');
            var split = (tail.Length + 1) / 2;
            result[^2] = string.Join(" ", tail.Take(split));
            result[^1] = string.Join(" ", tail.Skip(split));
        }
        return result.ToArray();
    }

    internal static void WriteWave(string path, IEnumerable<float> samples, int sampleRate)
    {
        using var writer = new BinaryWriter(File.Create(path));
        var data = samples.Select(v => (short)Math.Clamp(v * 32767, short.MinValue, short.MaxValue)).ToArray();
        writer.Write("RIFF"u8); writer.Write(36 + data.Length * 2); writer.Write("WAVEfmt "u8); writer.Write(16);
        writer.Write((short)1); writer.Write((short)1); writer.Write(sampleRate); writer.Write(sampleRate * 2);
        writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(data.Length * 2);
        foreach (var value in data) writer.Write(value);
    }

    // ---- Animated scene composition: deterministic, declarative layouts driven by narration cue times. ----

    internal sealed record ScenePlan(VideoScene Scene, int Index, int Count, double Start, double Speech, double[] Cues,
        (string Text, double Start)[] Captions)
    {
        public double Duration => Speech + ScenePause;

        public static ScenePlan Create(VideoStory story, int index, double start, double speech)
        {
            var scene = story.Scenes[index];
            var length = Math.Max(1, scene.Narration.Length);
            var cursor = 0;
            var cues = scene.Beats.Select(beat =>
            {
                var at = scene.Narration.IndexOf(beat.Cue, cursor, StringComparison.OrdinalIgnoreCase);
                if (at < 0)
                    throw new InvalidDataException($"Scene {index + 1} cue \"{beat.Cue}\" has no remaining narration occurrence.");
                cursor = at + beat.Cue.Length;
                return Math.Clamp(speech * at / length - 0.1, 0.15, Math.Max(0.15, speech - 0.4));
            }).ToArray();
            if (cues.Zip(cues.Skip(1)).Any(pair => pair.First >= pair.Second))
                throw new InvalidDataException($"Scene {index + 1} cues need distinct, increasing reveal times. Space their phrases further apart in the narration.");
            var parts = VideoRenderer.Captions(scene.Narration);
            var weight = Math.Max(1, parts.Sum(p => p.Length));
            var elapsed = 0.0;
            var captions = parts.Select(p => { var at = elapsed; elapsed += speech * p.Length / weight; return (p, at); }).ToArray();
            return new(scene, index, story.Scenes.Length, start, speech, cues, captions);
        }
    }

    // Measures every scene's fully revealed frame so oversized visual text is rejected before narration is generated.
    internal static void CheckLayout(VideoStory story)
    {
        using var bitmap = new Bitmap(VideoStory.Width, VideoStory.Height, PixelFormat.Format32bppArgb);
        var arts = story.Cast.Where(c => c is not null && c.CatId is not null).DistinctBy(c => c.CatId)
            .ToDictionary(c => c.CatId, c => CatArt.Placeholder(c.CatId), StringComparer.Ordinal);
        try
        {
            for (var index = 0; index < story.Scenes.Length; index++)
            {
                var plan = ScenePlan.Create(story, index, 0, 8);
                using var g = Graphics.FromImage(bitmap);
                DrawFrame(g, plan, plan.Duration - 0.01, arts, null);
            }
        }
        finally { foreach (var art in arts.Values) art.Dispose(); }
    }

    internal sealed record CatLook(RectangleF Frame, PointF Face, float FaceRadius);
    // Hand-measured framing for the bundled real photos (pixel coordinates); unknown assets use a centred default.
    static readonly Dictionary<string, CatLook> Looks = new(StringComparer.Ordinal)
    {
        ["yawn"] = new(new(40, 200, 880, 440), new(690, 415), 125),
        ["rugby"] = new(new(20, 10, 800, 820), new(415, 165), 130),
        ["cool"] = new(new(100, 120, 860, 1100), new(600, 590), 330),
        ["peek"] = new(new(110, 185, 740, 980), new(290, 385), 170),
        ["judge"] = new(new(180, 20, 650, 630), new(475, 265), 190),
        ["mischief"] = new(new(100, 120, 800, 540), new(520, 360), 185),
        ["cuddle"] = new(new(0, 70, 920, 630), new(255, 390), 210),
        ["duo"] = new(new(760, 310, 490, 320), new(1000, 460), 170),
    };

    internal static string Framing(string id) => Looks.TryGetValue(id, out var look) && look.Frame.Width > look.Frame.Height * 1.3f
        ? "wide landscape card; face close-up available" : "square-ish card; face close-up available";

    internal sealed class CatArt : IDisposable
    {
        public required Bitmap Card, Face;
        public required TextureBrush CardBrush, FaceBrush;

        public static CatArt Load(string id, string file)
        {
            using var photo = Image.FromFile(file);
            var look = Looks.TryGetValue(id, out var known) && known.Frame.Right <= photo.Width && known.Frame.Bottom <= photo.Height
                ? known : new CatLook(new(0, 0, photo.Width, photo.Height), new(photo.Width / 2f, photo.Height * 0.4f), Math.Min(photo.Width, photo.Height) / 4f);
            var scale = Math.Min(1f, 600f / Math.Max(look.Frame.Width, look.Frame.Height));
            var card = Crop(photo, look.Frame, (int)(look.Frame.Width * scale), (int)(look.Frame.Height * scale));
            var side = Math.Min(look.FaceRadius * 2.3f, Math.Min(photo.Width, photo.Height));
            var x = Math.Clamp(look.Face.X - side / 2, 0, photo.Width - side);
            var y = Math.Clamp(look.Face.Y - side / 2, 0, photo.Height - side);
            return Create(card, Crop(photo, new(x, y, side, side), 360, 360));
        }

        public static CatArt Placeholder(string id)
        {
            var frame = Looks.TryGetValue(id, out var look) ? look.Frame.Size : new SizeF(4, 3);
            var scale = 600f / Math.Max(frame.Width, frame.Height);
            return Create(new Bitmap((int)(frame.Width * scale), (int)(frame.Height * scale)), new Bitmap(360, 360));
        }

        static CatArt Create(Bitmap card, Bitmap face) => new()
        {
            Card = card, Face = face, CardBrush = new TextureBrush(card, WrapMode.Clamp), FaceBrush = new TextureBrush(face, WrapMode.Clamp),
        };

        static Bitmap Crop(Image photo, RectangleF source, int width, int height)
        {
            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bitmap);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(photo, new Rectangle(0, 0, width, height), source.X, source.Y, source.Width, source.Height, GraphicsUnit.Pixel);
            return bitmap;
        }

        public void Dispose() { CardBrush.Dispose(); FaceBrush.Dispose(); Card.Dispose(); Face.Dispose(); }
    }

    static readonly Color Old = Color.FromArgb(255, 168, 122), New = Color.FromArgb(126, 231, 196),
        Alert = Color.FromArgb(255, 122, 150), Paper = Color.FromArgb(251, 248, 240);

    static Color ToneColor(string tone, Color plain) => tone switch { "old" => Old, "new" => New, "alert" => Alert, _ => plain };
    static Color Fade(Color color, double alpha) => Color.FromArgb((int)Math.Clamp(color.A * alpha, 0, 255), color);
    static double Clamp01(double value) => Math.Clamp(value, 0, 1);
    static double EaseOut(double p) => 1 - Math.Pow(1 - Clamp01(p), 3);
    static double EaseInOut(double p) { p = Clamp01(p); return p < 0.5 ? 4 * p * p * p : 1 - Math.Pow(-2 * p + 2, 3) / 2; }
    static double Back(double p) { p = Clamp01(p); const double c = 1.70158; var x = p - 1; return 1 + (c + 1) * x * x * x + c * x * x; }
    static double Reveal(double t, double at, double length = 0.55) => Clamp01((t - at) / length);

    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    static Font Face(float size, bool heavy) =>
        new(heavy ? "Segoe UI Black" : "Segoe UI", size, heavy ? FontStyle.Regular : FontStyle.Bold, GraphicsUnit.Pixel);

    // Draws wrapped text, shrinking to fit; text that cannot fit at a readable size is a storyboard error.
    static SizeF Label(Graphics g, string text, float size, Color color, RectangleF rect, double alpha = 1, bool heavy = false,
        StringAlignment align = StringAlignment.Near, StringAlignment vertical = StringAlignment.Near, bool draw = true)
    {
        using var format = new StringFormat { Alignment = align, LineAlignment = vertical, Trimming = StringTrimming.None };
        for (var current = size; ; current -= 2)
        {
            using var font = Face(current, heavy);
            var measured = g.MeasureString(text, font, (int)rect.Width, format);
            if (measured.Height <= rect.Height + 1 && measured.Width <= rect.Width + 1)
            {
                if (draw && alpha > 0.004)
                {
                    using var brush = new SolidBrush(Fade(color, alpha));
                    g.DrawString(text, font, brush, rect, format);
                }
                return measured;
            }
            if (current - 2 < size * 0.62f)
                throw new InvalidDataException($"On-screen text \"{text}\" does not fit its animated layout; shorten it.");
        }
    }

    static void Glow(Graphics g, PointF center, float radius, Color color)
    {
        using var path = new GraphicsPath();
        path.AddEllipse(center.X - radius, center.Y - radius, radius * 2, radius * 2);
        using var brush = new PathGradientBrush(path) { CenterColor = color, SurroundColors = [Color.FromArgb(0, color)] };
        g.FillPath(brush, path);
    }

    internal static void DrawFrame(Graphics g, ScenePlan plan, double t, IReadOnlyDictionary<string, CatArt> arts, Bitmap? previous)
    {
        var scene = plan.Scene;
        var u = Clamp01(t / plan.Duration);
        g.ResetTransform();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        var drift = scene.Camera == "drift" ? (float)(18 - 36 * u) : 0f;
        using (var sky = new LinearGradientBrush(new Rectangle(0, 0, VideoStory.Width, VideoStory.Height),
            Color.FromArgb(11, 16, 29), Color.FromArgb(31, 35, 66), 35f))
            g.FillRectangle(sky, 0, 0, VideoStory.Width, VideoStory.Height);
        var palette = scene.Layout == "contrast" ? new[] { Old, New, Accent }
            : plan.Index % 2 == 0 ? new[] { New, Color.FromArgb(120, 150, 255), Old } : new[] { Color.FromArgb(120, 150, 255), New, Alert };
        Glow(g, new(180 - drift * 2.2f, 150 + plan.Index * 23 % 90), 430, Fade(palette[0], 0.16));
        Glow(g, new(1120 - drift * 2.2f, 560 - plan.Index * 37 % 120), 470, Fade(palette[1], 0.15));
        Glow(g, new(700 + plan.Index * 71 % 260 - drift, 80), 260, Fade(palette[2], 0.07));

        var stage = g.Save();
        var zoom = scene.Camera == "push" ? 1 + 0.045f * (float)EaseInOut(u) : 1f;
        g.TranslateTransform(VideoStory.Width / 2f + drift, 340);
        g.ScaleTransform(zoom, zoom);
        g.TranslateTransform(-VideoStory.Width / 2f, -340);
        switch (scene.Layout)
        {
            case "contrast": Contrast(g, plan, t, u, arts); break;
            case "journey": Journey(g, plan, t, u, arts); break;
            case "list": List(g, plan, t, u, arts); break;
            default: Spotlight(g, plan, t, u, arts); break;
        }
        g.Restore(stage);

        var enter = EaseOut(Reveal(t, 0, 0.45));
        using (var dot = new SolidBrush(Fade(New, enter))) g.FillEllipse(dot, 64, 45, 12, 12);
        Label(g, scene.Kicker.ToUpperInvariant(), 21, Color.FromArgb(205, 222, 235), new(86 - 20 * (float)(1 - enter), 36, 760, 32), enter);
        Label(g, "AI-generated explainer", 16, Color.FromArgb(150, 170, 190), new(940, 40, 276, 26), 0.8, align: StringAlignment.Far);
        var caption = plan.Captions.LastOrDefault(c => c.Start <= Math.Max(0, t)).Text ?? plan.Captions[0].Text;
        using (var font = Face(30, false))
        {
            var size = g.MeasureString(caption, font, 1080);
            var box = new RectangleF(640 - size.Width / 2 - 26, 688 - size.Height - 18, size.Width + 52, size.Height + 18);
            using var path = Rounded(box, 20);
            using var fill = new SolidBrush(Color.FromArgb(205, 7, 10, 18));
            g.FillPath(fill, path);
            using var text = new SolidBrush(Color.White);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(caption, font, text, box, format);
        }
        var segment = (VideoStory.Width - 128f - (plan.Count - 1) * 8) / plan.Count;
        for (var i = 0; i < plan.Count; i++)
        {
            var x = 64 + i * (segment + 8);
            using var track = new SolidBrush(Color.FromArgb(i < plan.Index ? 150 : 45, New));
            g.FillRectangle(track, x, 707, segment, 4);
            if (i == plan.Index)
            {
                using var fill = new SolidBrush(New);
                g.FillRectangle(fill, x, 707, segment * (float)u, 4);
            }
        }
        if (previous is not null && t < CrossFade)
        {
            using var attributes = new ImageAttributes();
            attributes.SetColorMatrix(new ColorMatrix { Matrix33 = (float)(1 - EaseInOut(t / CrossFade)) });
            g.DrawImage(previous, new Rectangle(0, 0, VideoStory.Width, VideoStory.Height), 0, 0, previous.Width, previous.Height,
                GraphicsUnit.Pixel, attributes);
        }
    }

    // Draws a real cat as a character; returns its on-screen bounds for speech-bubble placement.
    static RectangleF Cat(Graphics g, VideoCatShot shot, IReadOnlyDictionary<string, CatArt> arts, PointF center, float size,
        double arrive, double u, int fromSide, float tilt = 0)
    {
        if (shot.CatId == "" || arrive <= 0 || !arts.TryGetValue(shot.CatId, out var art)) return RectangleF.Empty;
        var a = EaseOut(arrive);
        float dx = 0, dy = 0, scale = 1;
        var alpha = Clamp01(arrive * 2.5);
        var push = 1.0;
        switch (shot.Action)
        {
            case "enter": dx = fromSide * (float)(1 - a) * 560; break;
            case "pop": scale = (float)Math.Max(0.05, Back(arrive)); alpha = Clamp01(arrive * 4); break;
            case "peek": dy = (float)(1 - a) * 420; break;
            default: push = 1 + 0.12 * EaseInOut(u); alpha = Clamp01(arrive * 1.5); break;
        }
        var face = shot.Shot == "face";
        var bitmap = face ? art.Face : art.Card;
        var fit = face ? 1 : Math.Min(size / bitmap.Width, size * 0.92f / bitmap.Height);
        var w = face ? size : bitmap.Width * fit;
        var h = face ? size : bitmap.Height * fit;
        var state = g.Save();
        g.TranslateTransform(center.X + dx, center.Y + dy);
        if (!face) g.RotateTransform(tilt);
        g.ScaleTransform(scale, scale);
        var rect = new RectangleF(-w / 2, -h / 2, w, h);
        using (var shadow = face ? new GraphicsPath() : Rounded(RectangleF.Inflate(rect, 12, 12), 24))
        {
            if (face) shadow.AddEllipse(RectangleF.Inflate(rect, 9, 9));
            for (var layer = 0; layer < 3; layer++)
            {
                using var shade = new SolidBrush(Color.FromArgb((int)(28 * alpha), 0, 0, 0));
                var offset = g.Save();
                g.TranslateTransform(4 + layer * 4, 8 + layer * 5);
                g.FillPath(shade, shadow);
                g.Restore(offset);
            }
            using var frame = new SolidBrush(Fade(face ? New : Paper, alpha));
            g.FillPath(frame, shadow);
        }
        using (var path = face ? new GraphicsPath() : Rounded(rect, 14))
        {
            if (face) path.AddEllipse(rect);
            var brush = face ? art.FaceBrush : art.CardBrush;
            TextureBrush? faded = alpha < 0.995 ? new TextureBrush(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                Attributes(alpha)) { WrapMode = WrapMode.Clamp } : null;
            var paint = faded ?? brush;
            paint.ResetTransform();
            paint.TranslateTransform(0, 0);
            paint.ScaleTransform((float)(w / bitmap.Width * push), (float)(h / bitmap.Height * push));
            paint.TranslateTransform(-bitmap.Width / 2f, -bitmap.Height / 2f);
            g.FillPath(paint, path);
            faded?.Dispose();
        }
        g.Restore(state);
        var shown = new SizeF(w * scale, h * scale);
        return new(center.X + dx - shown.Width / 2, center.Y + dy - shown.Height / 2, shown.Width, shown.Height);
    }

    static ImageAttributes Attributes(double alpha)
    {
        var attributes = new ImageAttributes();
        attributes.SetColorMatrix(new ColorMatrix { Matrix33 = (float)alpha });
        return attributes;
    }

    static void Bubble(Graphics g, string text, RectangleF cat, double p, bool above = true)
    {
        if (text.Length == 0 || p <= 0 || cat.IsEmpty) return;
        var measure = Label(g, text, 24, Color.Black, new(0, 0, 300, 72), draw: false);
        var w = measure.Width + 34; var h = measure.Height + 20;
        var inwardLeft = cat.X + cat.Width / 2 > VideoStory.Width / 2f;
        RectangleF box;
        PointF tip;
        if (above && cat.Top - h - 22 >= 84)
        {
            box = new(Math.Clamp(cat.X + cat.Width / 2 - w / 2 + (inwardLeft ? -40 : 40), 30, VideoStory.Width - 30 - w), cat.Top - h - 22, w, h);
            tip = new(cat.X + cat.Width / 2, cat.Top + 6);
        }
        else
        {
            box = new(inwardLeft ? cat.Left - w - 22 : cat.Right + 22, Math.Clamp(cat.Top + 4, 84, 560 - h), w, h);
            box.X = Math.Clamp(box.X, 30, VideoStory.Width - 30 - w);
            tip = new(inwardLeft ? cat.Left + 10 : cat.Right - 10, box.Y + h * 0.75f);
        }
        var s = (float)Math.Max(0.05, Back(p));
        var alpha = Clamp01(p * 3);
        var state = g.Save();
        g.TranslateTransform(tip.X, tip.Y);
        g.ScaleTransform(s, s);
        g.TranslateTransform(-tip.X, -tip.Y);
        using (var path = Rounded(box, 18))
        {
            var cx = Math.Clamp(tip.X, box.Left + 24, box.Right - 24);
            var cy = Math.Clamp(tip.Y, box.Top + 16, box.Bottom - 16);
            var horizontal = tip.Y > box.Top && tip.Y < box.Bottom;
            PointF[] tail = horizontal
                ? [new(tip.X < box.Left ? box.Left + 2 : box.Right - 2, cy - 12), new(tip.X < box.Left ? box.Left + 2 : box.Right - 2, cy + 12), tip]
                : [new(cx - 13, box.Bottom - 2), new(cx + 13, box.Bottom - 2), tip];
            path.AddPolygon(tail);
            path.FillMode = FillMode.Winding;
            using var fill = new SolidBrush(Fade(Paper, alpha));
            g.FillPath(fill, path);
        }
        Label(g, text, 24, Ink, new(box.X + 17, box.Y + 10, w - 30, h - 16), alpha);
        g.Restore(state);
    }

    static (float Left, float Width) TextColumn(VideoCatShot cat, float wide = 1040)
    {
        if (cat.CatId == "") return ((VideoStory.Width - wide) / 2, wide);
        return cat.Place == "left" ? (560f, 650f) : (80f, 640f);
    }

    static void Spotlight(Graphics g, ScenePlan plan, double t, double u, IReadOnlyDictionary<string, CatArt> arts)
    {
        var scene = plan.Scene;
        var term = scene.Layout == "term";
        var (left, width) = TextColumn(scene.Cat);
        var centered = scene.Cat.CatId == "" ? StringAlignment.Center : StringAlignment.Near;
        var p0 = EaseOut(Reveal(t, plan.Cues[0]));
        var main = scene.Beats[0];
        var mainColor = term ? ToneColor(main.Tone, New) : ToneColor(main.Tone, Color.White);
        Label(g, main.Text, term ? 76 : 64, mainColor, new(left, 160 + 34 * (float)(1 - p0), width, 210), p0, heavy: true,
            align: centered, vertical: StringAlignment.Far);
        var underline = (float)EaseOut(Reveal(t, plan.Cues[0] + 0.25, 0.6));
        using (var pen = new Pen(Fade(term ? mainColor : New, p0), 7) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            var x = centered == StringAlignment.Center ? 640 - 120 * underline : left + 4;
            if (underline > 0.01) g.DrawLine(pen, x, 392, x + 240 * underline, 392);
        }
        if (scene.Beats.Length > 1)
        {
            var p1 = EaseOut(Reveal(t, plan.Cues[1]));
            var second = scene.Beats[1];
            Label(g, second.Text, term ? 38 : 36, ToneColor(second.Tone, term ? Color.White : New),
                new(left + 24 * (float)(1 - p1) * (centered == StringAlignment.Center ? 0 : 1), 418, width, 150), p1, align: centered);
        }
        if (scene.Cat.CatId == "") return;
        var right = scene.Cat.Place == "right";
        var face = scene.Cat.Shot == "face";
        var center = new PointF(right ? 950 : 290, face ? 340 : 360);
        var rect = Cat(g, scene.Cat, arts, center, face ? 300 : 400, Reveal(t, 0.05, 0.8), u, right ? 1 : -1, right ? 3.5f : -3.5f);
        Bubble(g, scene.Cat.Bubble, rect, Reveal(t, 0.9, 0.45));
    }

    static void Contrast(Graphics g, ScenePlan plan, double t, double u, IReadOnlyDictionary<string, CatArt> arts)
    {
        var scene = plan.Scene;
        var before = EaseOut(Reveal(t, plan.Cues[0]));
        var after = EaseOut(Reveal(t, plan.Cues[1], 0.7));
        PointF[] leftSide = [new(0, 96), new(676, 96), new(604, 600), new(0, 600)];
        PointF[] rightSide = [new(676, 96), new(VideoStory.Width, 96), new(VideoStory.Width, 600), new(604, 600)];
        using (var warm = new SolidBrush(Fade(Old, 0.10 * before))) g.FillPolygon(warm, leftSide);
        var wipe = g.Save();
        g.SetClip(new RectangleF(604, 96, (VideoStory.Width - 604) * (float)after, 504));
        using (var cool = new SolidBrush(Fade(New, 0.12))) g.FillPolygon(cool, rightSide);
        g.Restore(wipe);
        using (var seam = new Pen(Fade(Color.White, 0.18 + 0.3 * after), 3)) g.DrawLine(seam, 676, 96 + 504 * (1 - (float)Math.Max(before, after)), 604, 600);
        void Side(string chip, VideoBeat beat, Color color, float x, float top, double p, double dim, int from)
        {
            if (p <= 0) return;
            var offset = from * 40 * (float)(1 - p);
            using (var font = Face(17, false))
            {
                var size = g.MeasureString(chip, font);
                var pill = new RectangleF(x + offset, top, size.Width + 26, 32);
                using var path = Rounded(pill, 16);
                using var pen = new Pen(Fade(color, p * dim), 2);
                g.DrawPath(pen, path);
                using var ink = new SolidBrush(Fade(color, p * dim));
                g.DrawString(chip, font, ink, pill.X + 13, pill.Y + 5);
            }
            Label(g, beat.Text, 44, ToneColor(beat.Tone, color), new(x + offset, top + 48, 500, 190), p * dim, heavy: true);
        }
        // The side the cat occupies keeps its label high; the open side centres its label in the space.
        var catSide = scene.Cat.CatId == "" ? "" : scene.Cat.Place;
        Side("BEFORE", scene.Beats[0], Old, 80, catSide == "before" ? 128 : 236, before, 1 - 0.5 * after, -1);
        Side("AFTER", scene.Beats[1], New, 724, catSide == "after" ? 128 : 236, after, 1, 1);
        if (after > 0.01)
        {
            using var arrow = new Pen(Fade(New, after), 7) { EndCap = LineCap.ArrowAnchor, StartCap = LineCap.Round };
            g.DrawLine(arrow, 590, 300, 590 + 110 * (float)after, 300);
        }
        if (scene.Cat.CatId == "") return;
        var onAfter = scene.Cat.Place == "after";
        var face = scene.Cat.Shot == "face";
        var rect = Cat(g, scene.Cat, arts, new(onAfter ? 1000 : 300, 478), face ? 190 : 250,
            Reveal(t, plan.Cues[onAfter ? 1 : 0], 0.8), u, onAfter ? 1 : -1, onAfter ? 3 : -3);
        Bubble(g, scene.Cat.Bubble, rect, Reveal(t, plan.Cues[onAfter ? 1 : 0] + 0.8, 0.45), above: false);
    }

    static void Journey(Graphics g, ScenePlan plan, double t, double u, IReadOnlyDictionary<string, CatArt> arts)
    {
        var scene = plan.Scene;
        var n = scene.Beats.Length;
        var spacing = (1110f - 170f) / (n - 1);
        var points = Enumerable.Range(0, n).Select(i => new PointF(170 + i * spacing, 380 + (i % 2 == 0 ? -22 : 22))).ToArray();
        var start = new PointF(70, points[0].Y);
        using (var faint = new Pen(Color.FromArgb(60, 255, 255, 255), 4) { DashStyle = DashStyle.Dot })
            g.DrawLines(faint, [start, .. points]);
        // Position along the path: the traveller hops to each station as its cue is spoken.
        var reached = -1; var hop = 1.0;
        for (var i = 0; i < n; i++)
            if (t >= plan.Cues[i]) { reached = i; hop = EaseInOut(Reveal(t, plan.Cues[i], 0.75)); }
        var from = reached <= 0 ? start : points[reached - 1];
        var to = reached < 0 ? start : points[reached];
        var traveller = new PointF(from.X + (to.X - from.X) * (float)hop, from.Y + (to.Y - from.Y) * (float)hop);
        using (var trail = new Pen(New, 6) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            var path = new List<PointF> { start };
            for (var i = 0; i < reached; i++) path.Add(points[i]);
            path.Add(traveller);
            if (path.Count > 1 && (traveller.X - start.X) > 1) g.DrawLines(trail, path.ToArray());
        }
        var labelWidth = Math.Min(300, spacing - 26);
        for (var i = 0; i < n; i++)
        {
            var p = Reveal(t, plan.Cues[i] + 0.35, 0.5);
            var color = ToneColor(scene.Beats[i].Tone, New);
            var radius = 24 * (float)(p > 0 ? Back(p) : 0.7);
            using (var ring = new Pen(Color.FromArgb(110, 255, 255, 255), 3)) g.DrawEllipse(ring, points[i].X - 24, points[i].Y - 24, 48, 48);
            if (p > 0)
            {
                using var fill = new SolidBrush(color);
                g.FillEllipse(fill, points[i].X - radius, points[i].Y - radius, radius * 2, radius * 2);
                Label(g, (i + 1).ToString(), 22, Ink, new(points[i].X - 24, points[i].Y - 16, 48, 34), Clamp01(p * 2),
                    align: StringAlignment.Center, vertical: StringAlignment.Center);
            }
            var e = EaseOut(p);
            Label(g, scene.Beats[i].Text, 30, Color.White, new(points[i].X - labelWidth / 2, points[i].Y + 44 + 20 * (float)(1 - e), labelWidth, 132),
                e, align: StringAlignment.Center);
        }
        if (scene.Cat.CatId == "") return;
        var lift = (float)(Math.Sin(Math.PI * hop) * 34);
        var rect = Cat(g, scene.Cat, arts, new(traveller.X, traveller.Y - 104 - lift), 132, Reveal(t, 0.05, 0.7), u, -1);
        Bubble(g, scene.Cat.Bubble, rect, Reveal(t, 0.9, 0.45), above: false);
    }

    static void List(Graphics g, ScenePlan plan, double t, double u, IReadOnlyDictionary<string, CatArt> arts)
    {
        var scene = plan.Scene;
        var (left, width) = scene.Cat.CatId == "" ? (200f, 880f) : TextColumn(scene.Cat);
        var n = scene.Beats.Length;
        const float height = 92, gap = 28;
        var top = 340 - (n * height + (n - 1) * gap) / 2;
        for (var i = 0; i < n; i++)
        {
            var p = EaseOut(Reveal(t, plan.Cues[i], 0.6));
            if (p <= 0) continue;
            var beat = scene.Beats[i];
            var color = ToneColor(beat.Tone, Color.FromArgb(150, 170, 255));
            var state = g.Save();
            var y = top + i * (height + gap);
            g.TranslateTransform(left - 70 * (float)(1 - p), y + height / 2);
            g.RotateTransform(i % 2 == 0 ? -0.6f : 0.6f);
            var pill = new RectangleF(0, -height / 2, width, height);
            using (var path = Rounded(pill, height / 2))
            using (var fill = new SolidBrush(Color.FromArgb((int)(24 * p), 255, 255, 255)))
            using (var edge = new Pen(Fade(color, 0.55 * p), 2))
            {
                g.FillPath(fill, path);
                g.DrawPath(edge, path);
            }
            using (var dot = new SolidBrush(Fade(color, p))) g.FillEllipse(dot, 30, -11, 22, 22);
            Label(g, beat.Text, 32, Color.White, new(72, -height / 2 + 6, width - 100, height - 12), p, vertical: StringAlignment.Center);
            g.Restore(state);
        }
        if (scene.Cat.CatId == "") return;
        var right = scene.Cat.Place == "right";
        var face = scene.Cat.Shot == "face";
        var rect = Cat(g, scene.Cat, arts, new(right ? 1000 : 280, face ? 330 : 360), face ? 280 : 380, Reveal(t, 0.05, 0.8), u,
            right ? 1 : -1, right ? 3 : -3);
        Bubble(g, scene.Cat.Bubble, rect, Reveal(t, 0.9, 0.45));
    }
}
