namespace AdoPrView;

internal sealed record CommentOutcome(string FindingId, string Status, string Message, string? Url);
internal sealed record InvestigationState(string Key, string ConversationId, string RequestId, long Sequence, string Status,
    string Message, ExplainRevision? Revision, string? Worktree, InvestigationTurn[] Turns, string[] Evidence, string[] Coverage,
    ExplanationAttachment[] ExplanationContexts, ReferencePrAttachment[] ReferencePrContexts);

internal sealed class InvestigationService(
    Func<string, CancellationToken, Task<IInvestigationSource>> capture,
    Func<string, ExplainRevision, string, string, string, LensFinding[], CommentOutcome[], CancellationToken, Task<CommentOutcome[]>> post,
    IInvestigationModel? model = null,
    Func<string, string, ExplanationAttachment>? resolveExplanation = null,
    Func<string, CancellationToken, Task<ReferencePrContext>>? captureReference = null,
    Func<string, string, int?, ExplanationAttachment>? resolveExplanationSection = null) : IAsyncDisposable
{
    sealed class Conversation(InvestigationState state)
    {
        public InvestigationState State = state;
        public IInvestigationSource? Source;
        public CancellationTokenSource? Cancel;
        public Task Work = Task.CompletedTask;
        public bool Closing;
        public ReferencePrContext[] References = [];
    }

    sealed class ReferencedSource(IInvestigationSource source, ReferencePrContext[] references) : IInvestigationSource
    {
        public ExplainContext Context => source.Context;
        public string Location => source.Location;
        public string[] Evidence => source.Evidence;
        public ReferencePrContext[] References => references;
        public Task<string> ReadFileAsync(string path, string side, int startLine, CancellationToken ct) => source.ReadFileAsync(path, side, startLine, ct);
        public Task<string> ListDirectoryAsync(string path, string side, CancellationToken ct) => source.ListDirectoryAsync(path, side, ct);
        public void ValidateFinding(LensFinding finding) => source.ValidateFinding(finding);
        public Task VerifyAsync(CancellationToken ct) => source.VerifyAsync(ct);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    readonly object _gate = new();
    readonly Dictionary<string, Conversation> _conversations = new(StringComparer.Ordinal);
    readonly IInvestigationModel _model = model ?? new InvestigationModel();
    long _sequence;
    bool _disposed;
    Task? _disposal;
    public event Action<InvestigationState>? Changed;
    internal static bool Active(string status) => status is "preparing" or "running" or "cancelling" or "posting";
    static InvestigationState Snapshot(InvestigationState state) => state with
    { ExplanationContexts = state.ExplanationContexts.Select(a => a.Snapshot()).ToArray(),
        Turns = state.Turns.Select(t => t with { ReferencePrUrls = t.ReferencePrUrls is null ? null : [.. t.ReferencePrUrls],
            ExplanationRequestIds = t.ExplanationRequestIds is null ? null : [.. t.ExplanationRequestIds] }).ToArray(),
        ReferencePrContexts = state.ReferencePrContexts.Select(a => a with { Evidence = [.. a.Evidence], Coverage = [.. a.Coverage] }).ToArray() };
    public InvestigationState[] States { get { lock (_gate) return _conversations.Values.Select(c => Snapshot(c.State)).ToArray(); } }

    public InvestigationState Ask(string key, string? conversationId, string kind, string question, string? explanationRequestId = null,
        int? explanationSectionIndex = null)
    {
        if (kind is not ("question" or "lens") || string.IsNullOrWhiteSpace(question) || question.Length > 4000)
            throw new ArgumentException("Enter a question or concrete review lens (up to 4,000 characters).");
        if (explanationSectionIndex is not null && explanationRequestId is null)
            throw new ArgumentException("Select an explanation request before selecting its section.");
        var urls = ReferencePrContext.QuestionUrls(question);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_conversations.Values.Any(c => Active(c.State.Status)))
                throw new InvalidOperationException("Finish or cancel the current investigation before starting another turn.");
            ExplanationAttachment? attachment = null;
            var existing = _conversations.GetValueOrDefault(key);
            if (explanationRequestId is not null)
            {
                if (string.IsNullOrWhiteSpace(explanationRequestId)) throw new ArgumentException("Select a completed explanation ID.");
                attachment = existing?.State.ExplanationContexts.SingleOrDefault(a => a.RequestId == explanationRequestId)?.Snapshot();
                if (attachment is not null && attachment.SectionIndex != explanationSectionIndex)
                    throw new InvalidOperationException("This explanation already has a pinned whole/section attachment in this conversation. Start a new conversation before changing its section.");
                attachment ??= (explanationSectionIndex is null ? resolveExplanation?.Invoke(key, explanationRequestId) :
                    resolveExplanationSection?.Invoke(key, explanationRequestId, explanationSectionIndex))?.Snapshot()
                    ?? throw new InvalidOperationException("The selected explanation or section is unavailable. Select a completed server-listed explanation section.");
                if (attachment.RequestId != explanationRequestId || attachment.Revision.Key != key)
                    throw new InvalidOperationException("The selected explanation belongs to another PR or request.");
                if (attachment.SectionIndex != explanationSectionIndex)
                    throw new InvalidOperationException("The server did not resolve the exact requested explanation section.");
            }
            if (!_conversations.TryGetValue(key, out var c))
            {
                if (!string.IsNullOrEmpty(conversationId)) throw new InvalidOperationException("This conversation is no longer available.");
                c = new(new(key, Guid.NewGuid().ToString("N"), "", 0, "preparing", "", null, null, [], [], [], [], []));
            }
            else if (c.State.ConversationId != conversationId)
                throw new InvalidOperationException("The selected PR conversation changed. Reopen it before sending.");
            var contexts = attachment is not null && !c.State.ExplanationContexts.Any(a => a.RequestId == attachment.RequestId)
                ? c.State.ExplanationContexts.Append(attachment).ToArray() : c.State.ExplanationContexts;
            var selectedId = explanationRequestId ?? c.State.Turns.LastOrDefault()?.ExplanationRequestId;
            if (c.References.Select(r => r.Url).Union(urls, StringComparer.Ordinal).Count() > 2)
                throw new InvalidOperationException("This conversation exceeds the two-reference-PR limit. Start a new conversation with fewer references.");
            RequireContextBudget(c.State.Turns, contexts, kind, question.Trim(), selectedId, c.State.ReferencePrContexts);
            if (c.State.Revision is not null && contexts.Any(a => a.Revision != c.State.Revision))
                throw new InvalidOperationException("The selected explanation revisions changed or do not match this pinned conversation. Start a new conversation and regenerate the explanation.");
            _conversations[key] = c;
            c.Cancel?.Dispose();
            c.Cancel = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            Set(c, s => s with { RequestId = Guid.NewGuid().ToString("N"), Status = "preparing",
                Message = c.Source is null ? "Preparing a pinned isolated WSL source worktree..." : "Checking the pinned PR revision before this follow-up..." });
            c.Work = Task.Run(() => RunAsync(c, kind, question.Trim(), contexts, selectedId, urls, c.Cancel.Token));
            return Snapshot(c.State);
        }
    }

    static void RequireContextBudget(InvestigationTurn[] turns, ExplanationAttachment[] contexts, string kind, string question,
        string? explanationRequestId, ReferencePrAttachment[] references)
    {
        if (turns.Length >= 12 || InvestigationModel.SerializeReplay(turns, contexts, kind, question, explanationRequestId, references).Length > 100000)
            throw new InvalidOperationException("This conversation reached its context limit, including explanations, reference PR evidence and JSON escaping. A new conversation removes prior history but cannot make an oversized package fit. Explicitly select a server-listed smaller section (which preserves the full ARM host section), or a smaller completed explanation.");
    }

    void Set(Conversation c, Func<InvestigationState, InvestigationState> update, Action? accepted = null)
    {
        InvestigationState state;
        lock (_gate)
        {
            var next = update(c.State);
            if (next.Status == "completed" && c.State.Status is "running" or "cancelling" && c.Cancel?.IsCancellationRequested == true)
                next = c.State with { Status = "cancelled", Message = "Investigation cancelled. No draft answer was accepted and no comments were posted." };
            if (next.Status == "completed") accepted?.Invoke();
            c.State = next with { Sequence = ++_sequence };
            state = Snapshot(c.State);
        }
        Changed?.Invoke(state);
    }

    async Task RunAsync(Conversation c, string kind, string question, ExplanationAttachment[] contexts,
        string? explanationRequestId, string[] urls, CancellationToken ct)
    {
        var pendingReferences = new List<ReferencePrContext>();
        try
        {
            c.Source ??= await capture(c.State.Key, ct);
            await c.Source.VerifyAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (contexts.Any(a => a.Revision != c.Source.Context.Revision))
                throw new InvalidOperationException("The selected explanation revisions changed or do not match the captured source. Generate a new explanation; no context-free fallback was used.");
            foreach (var url in urls.Where(u => !c.References.Any(r => r.Url == u)))
            {
                if (captureReference is null) throw new InvalidOperationException("Reference PR reading is unavailable. No unread URL was sent to the model.");
                var reference = await captureReference(url, ct);
                pendingReferences.Add(reference);
                if (reference.Url != url) throw new InvalidOperationException("Reference PR resolver returned a different URL identity.");
            }
            ReferencePrContext[] references = [.. c.References, .. pendingReferences];
            foreach (var reference in references) await reference.Context.VerifyAsync(ct);
            RequireContextBudget(c.State.Turns, contexts, kind, question, explanationRequestId, references.Select(r => r.Snapshot()).ToArray());
            ct.ThrowIfCancellationRequested();
            Set(c, s => s with { Status = "running", Message = "Copilot is inspecting pinned source. No comments are being posted.",
                Revision = c.Source.Context.Revision, Worktree = c.Source.Location });
            var answer = await _model.AskAsync(new ReferencedSource(c.Source, references), c.State.Turns, kind, question, ct,
                contexts.Select(a => a.Snapshot()).ToArray(), explanationRequestId);
            ct.ThrowIfCancellationRequested();
            await c.Source.VerifyAsync(ct);
            foreach (var reference in references) await reference.Context.VerifyAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(answer.Answer) || answer.Answer.Length > 32000)
                throw new InvalidOperationException("Copilot returned no bounded question answer.");
            InvestigationTurn[] turns = [.. c.State.Turns,
                new(c.State.RequestId, kind, question, answer.Answer,
                    answer.Findings.Select(f => f with { OutgoingComment = PrService.LensCommentContent(
                        c.Source.Context.Revision, c.State.ConversationId, c.State.RequestId, question, f) }).ToArray(), [], explanationRequestId,
                    references.Select(r => r.Url).ToArray(), contexts.Select(a => a.RequestId).ToArray(),
                    contexts.SingleOrDefault(a => a.RequestId == explanationRequestId)?.SectionIndex)];
            var referenceSnapshots = references.Select(r => r.Snapshot()).ToArray();
            if (InvestigationModel.SerializeReplay(turns, contexts, "", "", null, referenceSnapshots).Length > 100000)
                throw new InvalidOperationException("The answer exceeded this conversation's context limit. No answer or attachment was accepted. Start a new conversation.");
            Set(c, s => s with { Status = "completed",
                Message = kind == "lens" ? answer.Findings.Length == 0 ? "No findings established for this lens in the inspected scope. No comments posted."
                    : $"{answer.Findings.Length} preview finding(s). No comments posted. Select findings explicitly to post."
                    : "Answer ready. Follow-ups use this conversation's pinned source.",
                Turns = turns, ExplanationContexts = contexts, ReferencePrContexts = referenceSnapshots,
                Evidence = c.Source.Evidence, Coverage = c.Source.Context.Coverage() },
                () => { c.References = references; pendingReferences.Clear(); });
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
        finally { foreach (var reference in pendingReferences) reference.Dispose(); }
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
        return Snapshot(c.State);
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
        return Snapshot(c.State);
    }

    async Task ResetCoreAsync(Conversation c)
    {
        try
        {
            if (c.Source is not null) await c.Source.DisposeAsync();
            c.Source = null;
            foreach (var reference in c.References) reference.Dispose();
            c.References = [];
            c.Cancel?.Dispose();
            c.Cancel = null;
            Set(c, s => s with { ConversationId = Guid.NewGuid().ToString("N"), RequestId = "", Status = "completed",
                Message = "New conversation. The next question will capture fresh source.", Revision = null, Worktree = null, Turns = [], Evidence = [], Coverage = [], ExplanationContexts = [], ReferencePrContexts = [] });
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
        return Snapshot(c.State);
    }

    async Task PostCoreAsync(Conversation c, InvestigationTurn turn, LensFinding[] selected)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await c.Source!.VerifyAsync(timeout.Token);
            foreach (var reference in c.References) await reference.Context.VerifyAsync(timeout.Token);
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
        foreach (var c in all) { if (c.Source is not null) await c.Source.DisposeAsync(); foreach (var reference in c.References) reference.Dispose(); c.References = []; c.Cancel?.Dispose(); c.Cancel = null; }
        await _model.DisposeAsync();
    }
}
