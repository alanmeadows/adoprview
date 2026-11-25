namespace AdoPrView;

internal sealed record ExplainState(string Key, string RequestId, long Sequence, string Status, string Message,
    string Output, ExplainRevision? Revision, string[] Coverage, string[]? Evidence = null,
    ExplainSection[]? ExplanationSections = null);

internal sealed class ExplainService(Func<string, CancellationToken, Task<ExplainContext>> capture, IExplainModel? model = null) : IAsyncDisposable
{
    sealed class Operation(ExplainState state)
    {
        public ExplainState State = state;
        public readonly CancellationTokenSource Cancel = new();
        public Task Work = Task.CompletedTask;
        public bool CancelRequested;
        public long LastStream;
        public ExplainSections? Sections;
    }

    sealed record Artifact(ExplainState State, ExplainSections Sections);
    readonly object _gate = new();
    readonly Dictionary<string, Operation> _operations = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<Artifact>> _artifacts = new(StringComparer.Ordinal);
    readonly IExplainModel _model = model ?? new ExplainModel();
    bool _disposed;
    Task? _disposeTask;
    long _sequence;
    public event Action<ExplainState>? Changed;

    internal static bool Active(string status) => status is "preparing" or "running" or "cancelling";
    public ExplainState[] States { get { lock (_gate) return _operations.Values.Select(o => Snapshot(o.State)).ToArray(); } }
    public ExplainState[] Artifacts { get { lock (_gate) return _artifacts.Values.SelectMany(a => a).Select(a => Snapshot(a.State)).ToArray(); } }

    static ExplainState Snapshot(ExplainState state) => state with { Coverage = [.. state.Coverage], Evidence = [.. state.Evidence ?? []],
        ExplanationSections = state.ExplanationSections is null ? null : [.. state.ExplanationSections] };

    public ExplanationAttachment Resolve(string key, string requestId) => Resolve(key, requestId, null);

    public ExplanationAttachment Resolve(string key, string requestId, int? sectionIndex)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var artifact = _artifacts.GetValueOrDefault(key)?.SingleOrDefault(a => a.State.RequestId == requestId);
            var state = artifact?.State;
            if (state is null || state.Status != "completed" || state.Revision is null || state.Revision.Key != key)
                throw new InvalidOperationException("The selected explanation is missing, expired, unfinished, or belongs to another PR. Generate a completed explanation before attaching it.");
            if (sectionIndex is int index) return artifact!.Sections.Attach(state, index);
            return new(state.RequestId, state.Output, state.Revision, [.. state.Evidence ?? []], [.. state.Coverage]);
        }
    }

    public ExplainState Start(string key)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_operations.TryGetValue(key, out var previous) && Active(previous.State.Status)) return previous.State;
            // Share the runtime without allowing concurrent model requests.
            if (_operations.Values.Any(o => Active(o.State.Status)))
                throw new InvalidOperationException("Another PR explanation is running. Cancel it or wait for it to finish.");
            previous?.Cancel.Dispose();
            var operation = new Operation(new(key, Guid.NewGuid().ToString("N"), ++_sequence, "preparing",
                "Capturing the PR's source and merge-base revisions...", "", null, []));
            _operations[key] = operation;
            operation.Work = Task.Run(() => RunAsync(operation));
            return operation.State;
        }
    }

    void Update(Operation op, Func<ExplainState, ExplainState> change, bool stream = false)
    {
        ExplainState state;
        lock (_gate)
        {
            if (!_operations.TryGetValue(op.State.Key, out var current) || !ReferenceEquals(current, op)) return;
            op.State = change(op.State) with { Sequence = ++_sequence };
            if (op.CancelRequested && Active(op.State.Status))
                op.State = op.State with { Status = "cancelling", Message = "Cancelling the explanation..." };
            if (op.CancelRequested && op.State.Status == "completed")
                op.State = op.State with { Status = "cancelled", Message = "Explanation cancelled. Partial text is not a finished explanation." };
            if (op.State.Status == "completed")
            {
                if (!_artifacts.TryGetValue(op.State.Key, out var artifacts)) _artifacts[op.State.Key] = artifacts = [];
                artifacts.Add(new(Snapshot(op.State), op.Sections!));
                if (artifacts.Count > 12) artifacts.RemoveAt(0);
            }
            if (stream && Environment.TickCount64 - op.LastStream < 100) return;
            op.LastStream = Environment.TickCount64;
            state = Snapshot(op.State);
        }
        Changed?.Invoke(state);
    }

    async Task RunAsync(Operation op)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, op.Cancel.Token);
        ExplainContext? context = null;
        try
        {
            context = await capture(op.State.Key, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            Update(op, s => s with { Status = "running", Message = "Reading pinned diffs with Copilot...", Revision = context.Revision, Coverage = context.Coverage() });
            var result = await _model.ExplainAsync(context, text => Update(op, s =>
            {
                if (s.Output.Length + text.Length > 64_000) throw new InvalidOperationException("Copilot output exceeded 64,000 characters; explanation stopped.");
                return s with { Output = s.Output + text };
            }, stream: true), linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            await context.VerifyAsync(linked.Token);
            var original = result;
            result = context.Arm.Complete(result, context.Changes);
            if (result.Length > 64_000) throw new InvalidOperationException("Copilot output exceeded the explanation limit.");
            op.Sections = ExplainSections.Create(result, result == original ? null : original.TrimEnd().Length + 2);
            Update(op, s => s with { Status = "completed", Message = "Investigation ready. See inspected evidence and unread areas below.",
                Output = result, Coverage = context.Coverage(), Evidence = context.Evidence(), ExplanationSections = op.Sections.Metadata });
        }
        catch (OperationCanceledException)
        {
            Update(op, s => s with { Status = op.CancelRequested ? "cancelled" : "failed",
                Message = op.CancelRequested ? "Explanation cancelled. Partial text is not a finished explanation." : "Explanation timed out. Retry with a smaller PR.",
                Coverage = context?.Coverage() ?? [], Evidence = context?.Evidence() ?? [] });
        }
        catch (Exception ex)
        {
            Update(op, s => s with { Status = "failed", Message = "Explanation failed: " + ex.Message,
                Coverage = context?.Coverage() ?? [], Evidence = context?.Evidence() ?? [] });
        }
        finally { context?.Dispose(); }
    }

    public async Task<ExplainState> CancelAsync(string key, string requestId)
    {
        Operation op;
        lock (_gate)
        {
            if (!_operations.TryGetValue(key, out op!) || op.State.RequestId != requestId)
                throw new InvalidOperationException("This explanation is no longer current.");
            if (!Active(op.State.Status)) return Snapshot(op.State);
            op.CancelRequested = true;
            op.Cancel.Cancel();
        }
        Update(op, s => s with { });
        await op.Work;
        lock (_gate) return Snapshot(op.State);
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            _disposed = true;
            var operations = _operations.Values.ToArray();
            foreach (var op in operations) { op.CancelRequested = true; op.Cancel.Cancel(); }
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
