namespace AdoPrView;

// One browser message in flight; pending updates replace older snapshots of the same run.
internal sealed class CopilotRunDelivery(Func<CopilotRunState, long, Task> post, Action<Exception> report,
    bool waitForReady = false) : IAsyncDisposable
{
    readonly object gate = new();
    readonly Dictionary<string, CopilotRunState> pending = new(StringComparer.Ordinal);
    readonly CancellationTokenSource lifetime = new();
    Task worker = Task.CompletedTask;
    TaskCompletionSource? received;
    CopilotRunState? inFlight;
    long sequence;
    bool working, disposed;
    bool ready = !waitForReady;

    public void Offer(CopilotRunState state)
    {
        lock (gate)
        {
            if (disposed) return;
            pending[state.RunId] = state;
            if (working || !ready) return;
            working = true;
            worker = Task.Run(DrainAsync);
        }
    }

    public void Ready()
    {
        lock (gate)
        {
            if (disposed) return;
            ready = true;
            // Reload/init is an explicit resynchronization, not an automatic retry loop.
            if (inFlight is { } state) pending.TryAdd(state.RunId, state);
            received?.TrySetResult();
            if (working || pending.Count == 0) return;
            working = true;
            worker = Task.Run(DrainAsync);
        }
    }

    public void Acknowledge(long id)
    {
        lock (gate)
            if (id == sequence) received?.TrySetResult();
    }

    async Task DrainAsync()
    {
        try
        {
            while (true)
            {
                CopilotRunState state;
                long id;
                Task acknowledgement;
                lock (gate)
                {
                    if (disposed || !ready || pending.Count == 0) { working = false; return; }
                    var next = pending.First();
                    pending.Remove(next.Key);
                    state = next.Value;
                    inFlight = state;
                    id = ++sequence;
                    received = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    acknowledgement = received.Task;
                }
                await post(state, id).WaitAsync(lifetime.Token).ConfigureAwait(false);
                await acknowledgement.WaitAsync(lifetime.Token).ConfigureAwait(false);
                lock (gate) inFlight = null;
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            lock (gate)
            {
                if (inFlight is { } state) pending.TryAdd(state.RunId, state);
                inFlight = null;
                working = false; ready = false;
            }
            report(ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task completion;
        lock (gate)
        {
            if (disposed) return;
            disposed = true; pending.Clear(); completion = worker;
        }
        lifetime.Cancel();
        await completion.ConfigureAwait(false);
        lifetime.Dispose();
    }
}
