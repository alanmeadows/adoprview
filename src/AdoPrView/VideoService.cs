using System.IO;
using System.Text.Json;

namespace AdoPrView;

internal sealed record VideoResources(string Catalog, string[] CatIds, string Voice);
internal interface IVideoRenderer
{
    Task<VideoResources> PrepareAsync(CancellationToken ct);
    Task RenderAsync(VideoStory story, string work, string output, Action<string> progress, CancellationToken ct);
    Task<double> ValidateAsync(string path, CancellationToken ct);
}

internal sealed record VideoState(string Key, string RequestId, long Sequence, string Status, string Message,
    ExplainRevision? Revision, string[] Evidence, string[] Coverage, string? Path = null, string? MediaUrl = null,
    double? DurationSeconds = null, string? Voice = null, string Model = VideoStory.Model);

internal sealed class VideoService(Func<string, CancellationToken, Task<ExplainContext>> capture,
    Action<string> checkAvailable, VideoArtifacts artifacts, IVideoRenderer renderer, IVideoModel? model = null) : IAsyncDisposable
{
    sealed class Operation(VideoState state)
    {
        public VideoState State = state;
        public readonly CancellationTokenSource Cancel = new();
        public Task Work = Task.CompletedTask;
    }
    readonly object _gate = new();
    readonly Dictionary<string, Operation> _operations = new(StringComparer.Ordinal);
    readonly IVideoModel _model = model ?? new VideoModel();
    long _sequence;
    bool _disposed;
    Task? _disposeTask;
    public event Action<VideoState>? Changed;
    internal static bool Active(string status) => status is "preparing" or "storyboarding" or "rendering" or "cancelling";
    public VideoState[] States { get { lock (_gate) return _operations.Values.Select(o => o.State).ToArray(); } }

    public VideoState Start(string key)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_operations.TryGetValue(key, out var old) && Active(old.State.Status)) return old.State;
            checkAvailable(key);
            if (_operations.Values.Any(o => Active(o.State.Status)))
                throw new InvalidOperationException("Another PR video is generating. Wait for it to finish or cancel it first.");
            old?.Cancel.Dispose();
            var op = new Operation(new(key, Guid.NewGuid().ToString("N"), ++_sequence, "preparing",
                "Checking video dependencies and the selected Azure Speech connection...", null, [], []));
            _operations[key] = op;
            op.Work = Task.Run(() => RunAsync(op));
            return op.State;
        }
    }

    void Update(Operation op, Func<VideoState, VideoState> change)
    {
        VideoState state;
        lock (_gate)
        {
            if (!_operations.TryGetValue(op.State.Key, out var current) || !ReferenceEquals(current, op)) return;
            op.State = change(op.State) with { Sequence = ++_sequence };
            if (op.Cancel.IsCancellationRequested && Active(op.State.Status))
                op.State = op.State with { Status = "cancelling", Message = "Cancelling video generation..." };
            state = op.State;
        }
        Changed?.Invoke(state);
    }

    async Task RunAsync(Operation op)
    {
        ExplainContext? context = null;
        var ct = op.Cancel.Token;
        var id = op.State.RequestId;
        try
        {
            var resources = await renderer.PrepareAsync(ct);
            context = await capture(op.State.Key, ct);
            ct.ThrowIfCancellationRequested();
            artifacts.CreateWork(id);
            var work = artifacts.Work(id);
            Update(op, s => s with { Status = "storyboarding", Message = "Opus 5.5 is reading pinned PR source...",
                Revision = context.Revision, Voice = resources.Voice });
            var story = await _model.CreateAsync(context, work, resources.Catalog, resources.CatIds,
                message => Update(op, s => s with { Message = message }), ct);
            story.Validate(resources.CatIds);
            if (!context.Evidence().Any(s => s.StartsWith("Diff: ", StringComparison.Ordinal)) || context.ContextReads == 0)
                throw new InvalidOperationException("The video has no verified source-tool evidence. No video was rendered.");
            await context.VerifyAsync(ct);
            checkAvailable(op.State.Key);
            await File.WriteAllTextAsync(System.IO.Path.Combine(work, "storyboard.json"), JsonSerializer.Serialize(story, Bridge.Json), ct);
            await File.WriteAllTextAsync(System.IO.Path.Combine(work, "source-evidence.json"), JsonSerializer.Serialize(new
            {
                model = VideoStory.Model, context.Revision, evidence = context.Evidence(), coverage = context.Coverage(),
                context.DiffReads, context.ContextReads, voice = resources.Voice,
            }, Bridge.Json), ct);
            Update(op, s => s with { Status = "rendering", Message = "Generating natural narration and rendering the video...",
                Evidence = context.Evidence(), Coverage = context.Coverage() });
            await renderer.RenderAsync(story, work, artifacts.Pending(id),
                message => Update(op, s => s with { Message = message }), ct);
            var duration = await renderer.ValidateAsync(artifacts.Pending(id), ct);
            await context.VerifyAsync(ct);
            checkAvailable(op.State.Key);
            lock (_gate)
            {
                ct.ThrowIfCancellationRequested();
                var artifact = artifacts.Publish(id);
                op.State = op.State with { Sequence = ++_sequence, Status = "completed", Message = "Video ready. AI-generated; verify important details against the code.",
                    Path = artifact.Path, MediaUrl = artifact.Url, DurationSeconds = duration, Evidence = context.Evidence(), Coverage = context.Coverage() };
            }
            Changed?.Invoke(op.State);
        }
        catch (OperationCanceledException)
        {
            Update(op, s => s with { Status = ct.IsCancellationRequested ? "cancelled" : "failed",
                Message = ct.IsCancellationRequested ? "Video cancelled. Partial output is not published." : "A video operation timed out. Check prerequisites and retry.",
                Evidence = context?.Evidence() ?? [], Coverage = context?.Coverage() ?? [] });
        }
        catch (Exception ex)
        {
            Update(op, s => s with { Status = "failed", Message = "Video generation failed: " + ex.Message,
                Evidence = context?.Evidence() ?? [], Coverage = context?.Coverage() ?? [] });
        }
        finally { context?.Dispose(); }
    }

    public Task<VideoState> CancelAsync(string key, string requestId)
    {
        Operation op;
        lock (_gate)
        {
            if (!_operations.TryGetValue(key, out op!) || op.State.RequestId != requestId)
                throw new InvalidOperationException("This video job is no longer current.");
            if (!Active(op.State.Status)) return Task.FromResult(op.State);
            op.Cancel.Cancel();
        }
        Update(op, s => s with { });
        return Task.FromResult(op.State); // Cancellation is observable immediately; terminal state arrives as an event.
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            _disposed = true;
            var operations = _operations.Values.ToArray();
            foreach (var op in operations) op.Cancel.Cancel();
            _disposeTask = DisposeOperationsAsync(operations);
            return new(_disposeTask);
        }
    }

    async Task DisposeOperationsAsync(Operation[] operations)
    {
        await Task.WhenAll(operations.Select(o => o.Work));
        await _model.DisposeAsync();
        foreach (var op in operations) op.Cancel.Dispose();
    }
}
