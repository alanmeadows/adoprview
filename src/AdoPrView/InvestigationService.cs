namespace AdoPrView;

internal sealed record CommentOutcome(string FindingId, string Status, string Message, string? Url);
internal sealed record InvestigationState(string Key, string ConversationId, string RequestId, long Sequence, string Status,
    string Message, ExplainRevision? Revision, string? Worktree, InvestigationTurn[] Turns, string[] Evidence, string[] Coverage);

internal sealed class InvestigationService(
    Func<string, CancellationToken, Task<IInvestigationSource>> capture,
    Func<string, ExplainRevision, string, string, string, LensFinding[], CommentOutcome[], CancellationToken, Task<CommentOutcome[]>> post,
    IInvestigationModel? model = null) : IAsyncDisposable
{
    sealed class Conversation(InvestigationState state)
    {
        public InvestigationState State = state;
        public IInvestigationSource? Source;
        public CancellationTokenSource? Cancel;
        public Task Work = Task.CompletedTask;
        public bool Closing;
    }

    readonly object _gate = new();
    readonly Dictionary<string, Conversation> _conversations = new(StringComparer.Ordinal);
    readonly IInvestigationModel _model = model ?? new InvestigationModel();
    long _sequence;
    bool _disposed;
    Task? _disposal;
    public event Action<InvestigationState>? Changed;
    internal static bool Active(string status) => status is "preparing" or "running" or "cancelling" or "posting";
    public InvestigationState[] States { get { lock (_gate) return _conversations.Values.Select(c => c.State).ToArray(); } }

    public InvestigationState Ask(string key, string? conversationId, string kind, string question)
    {
        if (kind is not ("question" or "lens") || string.IsNullOrWhiteSpace(question) || question.Length > 4000)
            throw new ArgumentException("Enter a question or concrete review lens (up to 4,000 characters).");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_conversations.Values.Any(c => Active(c.State.Status)))
                throw new InvalidOperationException("Finish or cancel the current investigation before starting another turn.");
            if (!_conversations.TryGetValue(key, out var c))
            {
                if (!string.IsNullOrEmpty(conversationId)) throw new InvalidOperationException("This conversation is no longer available.");
                c = new(new(key, Guid.NewGuid().ToString("N"), "", 0, "preparing", "", null, null, [], [], []));
                _conversations[key] = c;
            }
            else if (c.State.ConversationId != conversationId)
                throw new InvalidOperationException("The selected PR conversation changed. Reopen it before sending.");
            if (c.State.Turns.Length >= 12 || c.State.Turns.Sum(t => t.Answer.Length + t.Question.Length) > 100000)
                throw new InvalidOperationException("This conversation reached its context limit. Start a new conversation.");
            c.Cancel?.Dispose();
            c.Cancel = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            Set(c, s => s with { RequestId = Guid.NewGuid().ToString("N"), Status = "preparing",
                Message = c.Source is null ? "Preparing a pinned isolated WSL source worktree..." : "Checking the pinned PR revision before this follow-up..." });
            c.Work = Task.Run(() => RunAsync(c, kind, question.Trim(), c.Cancel.Token));
            return c.State;
        }
    }

    void Set(Conversation c, Func<InvestigationState, InvestigationState> update)
    {
        InvestigationState state;
        lock (_gate)
        {
            var next = update(c.State);
            if (next.Status == "completed" && c.State.Status is "running" or "cancelling" && c.Cancel?.IsCancellationRequested == true)
                next = c.State with { Status = "cancelled", Message = "Investigation cancelled. No draft answer was accepted and no comments were posted." };
            c.State = next with { Sequence = ++_sequence };
            state = c.State;
        }
        Changed?.Invoke(state);
    }

    async Task RunAsync(Conversation c, string kind, string question, CancellationToken ct)
    {
        try
        {
            c.Source ??= await capture(c.State.Key, ct);
            await c.Source.VerifyAsync(ct);
            ct.ThrowIfCancellationRequested();
            Set(c, s => s with { Status = "running", Message = "Copilot is inspecting pinned source. No comments are being posted.",
                Revision = c.Source.Context.Revision, Worktree = c.Source.Location });
            var answer = await _model.AskAsync(c.Source, c.State.Turns, kind, question, ct);
            ct.ThrowIfCancellationRequested();
            await c.Source.VerifyAsync(ct);
            ct.ThrowIfCancellationRequested();
            Set(c, s => s with { Status = "completed",
                Message = kind == "lens" ? answer.Findings.Length == 0 ? "No findings established for this lens in the inspected scope. No comments posted."
                    : $"{answer.Findings.Length} preview finding(s). No comments posted. Select findings explicitly to post."
                    : "Answer ready. Follow-ups use this conversation's pinned source.",
                Turns = [.. s.Turns, new(s.RequestId, kind, question, answer.Answer, answer.Findings, [])],
                Evidence = c.Source.Evidence, Coverage = c.Source.Context.Coverage() });
        }
        catch (OperationCanceledException)
        {
            Set(c, s => s with { Status = "cancelled", Message = "Investigation cancelled or timed out. No draft answer was accepted and no comments were posted.",
                Evidence = c.Source?.Evidence ?? [], Coverage = c.Source?.Context.Coverage() ?? [] });
        }
        catch (Exception ex)
        {
            Set(c, s => s with { Status = "failed", Message = "Investigation failed: " + ex.Message,
                Evidence = c.Source?.Evidence ?? [], Coverage = c.Source?.Context.Coverage() ?? [] });
        }
    }

    Conversation Current(string key, string conversationId)
    {
        if (!_conversations.TryGetValue(key, out var c) || c.State.ConversationId != conversationId)
            throw new InvalidOperationException("The selected PR conversation is no longer current.");
        return c;
    }

    public async Task<InvestigationState> CancelAsync(string key, string conversationId, string requestId)
    {
        Conversation c;
        lock (_gate)
        {
            c = Current(key, conversationId);
            if (c.State.RequestId != requestId) throw new InvalidOperationException("This investigation turn is no longer current.");
            if (c.State.Status == "posting") throw new InvalidOperationException("Posting cannot be cancelled mid-request. Wait for the exact result.");
            if (c.Closing) throw new InvalidOperationException("Owned worktree cleanup is finishing. Wait for its exact result.");
            if (Active(c.State.Status)) { c.Cancel!.Cancel(); Set(c, s => s with { Status = "cancelling", Message = "Cancelling source inspection..." }); }
        }
        await c.Work;
        return c.State;
    }

    public async Task<InvestigationState> ResetAsync(string key, string conversationId)
    {
        Conversation c;
        lock (_gate)
        {
            c = Current(key, conversationId);
            if (Active(c.State.Status)) throw new InvalidOperationException("Finish or cancel this turn before starting a new conversation.");
            c.Closing = true;
            Set(c, s => s with { Status = "preparing", Message = "Closing the owned source worktree..." });
            c.Work = ResetCoreAsync(c);
        }
        await c.Work;
        return c.State;
    }

    async Task ResetCoreAsync(Conversation c)
    {
        try
        {
            if (c.Source is not null) await c.Source.DisposeAsync();
            c.Source = null;
            c.Cancel?.Dispose();
            c.Cancel = null;
            Set(c, s => s with { ConversationId = Guid.NewGuid().ToString("N"), RequestId = "", Status = "completed",
                Message = "New conversation. The next question will capture fresh source.", Revision = null, Worktree = null, Turns = [], Evidence = [], Coverage = [] });
        }
        catch (Exception ex) { Set(c, s => s with { Status = "failed", Message = "Worktree cleanup failed; it was retained: " + ex.Message }); }
        finally { c.Closing = false; }
    }

    public async Task<InvestigationState> PostAsync(string key, string conversationId, string turnId, string[] ids, bool confirmed)
    {
        Conversation c;
        InvestigationTurn turn;
        LensFinding[] selected;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            c = Current(key, conversationId);
            if (!confirmed || ids.Length == 0 || ids.Distinct().Count() != ids.Length)
                throw new ArgumentException("Explicitly select and confirm the findings to post.");
            if (_conversations.Values.Any(x => Active(x.State.Status))) throw new InvalidOperationException("Wait for the current operation before posting.");
            turn = c.State.Turns.LastOrDefault() ?? throw new InvalidOperationException("No lens preview is available.");
            if (turn.Id != turnId || turn.Kind != "lens" || c.State.Status != "completed" || c.Source is null)
                throw new InvalidOperationException("This lens preview is no longer current. Generate a fresh preview.");
            selected = ids.Select(id => turn.Findings.SingleOrDefault(f => f.Id == id) ??
                throw new ArgumentException("A selected finding is not in this preview.")).ToArray();
            foreach (var finding in selected) c.Source.ValidateFinding(finding);
            Set(c, s => s with { Status = "posting", Message = "Posting only the selected, confirmed findings; checking current revisions..." });
            c.Work = PostCoreAsync(c, turn, selected);
        }
        await c.Work;
        return c.State;
    }

    async Task PostCoreAsync(Conversation c, InvestigationTurn turn, LensFinding[] selected)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await c.Source!.VerifyAsync(timeout.Token);
            var outcomes = await post(c.State.Key, c.State.Revision!, c.State.ConversationId, turn.Id, turn.Question, selected, turn.Outcomes, timeout.Token);
            var all = turn.Outcomes.Where(o => !selected.Any(f => f.Id == o.FindingId)).Concat(outcomes).ToArray();
            var posted = all.Count(o => o.Status == "posted");
            Set(c, s => s with { Status = "completed", Message = $"{posted} comment(s) confirmed on ADO; {all.Count(o => o.Status != "posted")} failed or uncertain. {turn.Findings.Length - all.Length} not selected.",
                Turns = [.. s.Turns[..^1], turn with { Outcomes = all }] });
        }
        catch (Exception ex) { Set(c, s => s with { Status = "failed", Message = "Posting blocked; no additional comments confirmed: " + ex.Message }); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposed = true;
            if (_disposal is null || _disposal.IsFaulted) _disposal = DisposeCoreAsync();
            return new(_disposal);
        }
    }

    async Task DisposeCoreAsync()
    {
        Conversation[] all;
        lock (_gate) { all = _conversations.Values.ToArray(); foreach (var c in all) if (c.State.Status != "posting") c.Cancel?.Cancel(); }
        await Task.WhenAll(all.Select(c => c.Work));
        foreach (var c in all) { if (c.Source is not null) await c.Source.DisposeAsync(); c.Cancel?.Dispose(); c.Cancel = null; }
        await _model.DisposeAsync();
    }
}
