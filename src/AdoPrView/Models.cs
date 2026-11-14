namespace AdoPrView;

public enum Lane { NeedsHuman = 0, Blocked = 1, Waiting = 2, Ready = 3 }

public sealed record Person(string Name, string? UniqueName, string? Id);

public sealed record Gate(string State, string Label, string? Detail = null);   // State: ok|fail|pending|human|warn|na

public sealed record PolicyView(
    string EvaluationId, string Kind, string Name, string Status, bool IsBlocking,
    int? BuildId, string? BuildUrl, bool IsExpired, bool IsAutomatedReview = false, string? StatusDescription = null,
    bool RequiresLatestApproval = false);

public sealed record FailedTask(string Name, string Path, string Result, int? LogId, List<string> Issues);

public sealed record FailedBuild(int BuildId, string Name, string Url, string? EvaluationId, List<FailedTask> Tasks);

public sealed record ThreadView(
    int Id, string Status, string Author, bool IsBot, bool IsAgency, string Content, int Replies,
    string? FilePath, int? Line, DateTimeOffset Updated, string Url);

public sealed record ReviewerView(string Name, int Vote, bool IsRequired, bool IsContainer, string? Id = null);

public sealed record AgencyView(string State, int? Iteration, int? CurrentIteration, string Detail);

public sealed class PrView
{
    public required string Key { get; init; }
    public required int Id { get; init; }
    public required string Title { get; init; }
    public required string Url { get; init; }
    public required string Org { get; init; }
    public required string Project { get; init; }
    public required string ProjectId { get; init; }
    public required string Repo { get; init; }
    public required string RepoId { get; init; }
    public string SourceBranch { get; init; } = "";
    public string TargetBranch { get; init; } = "";
    public required Person Author { get; init; }
    public bool IsMine { get; init; }
    public bool IsHidden { get; init; }
    public bool IsActive { get; set; } = true;
    public ApprovalOffer? Approval { get; set; }
    public bool IsDraft { get; set; }
    public bool IsIgnoredDraft { get; set; }
    public DateTimeOffset Created { get; init; }
    public DateTimeOffset Updated { get; set; }
    public string Description { get; init; } = "";
    public string MergeStatus { get; set; } = "";
    public string? MergeFailure { get; set; }
    public bool MergeStale { get; set; }
    public string? LastMergeTargetCommit { get; set; }
    public string? CurrentTargetCommit { get; set; }
    public string? MergeCheckError { get; set; }
    public DateTimeOffset? MergeRecheckRequestedAt { get; set; }
    public bool MergeRecheckPending { get; set; }
    public string? MergeRecheckError { get; set; }
    public string? AutoCompleteBy { get; init; }
    public string? MergeStrategy { get; init; }
    public Lane Lane { get; set; }
    public List<string> Blockers { get; } = new();
    public List<string> Waiting { get; } = new();
    public List<string> HumanActions { get; } = new();   // reviews | pop | autocomplete | publish
    public Dictionary<string, Gate> Gates { get; } = new();
    public List<PolicyView> Policies { get; } = new();
    public List<FailedBuild> FailedBuilds { get; } = new();
    public List<ThreadView> Threads { get; } = new();
    public List<ReviewerView> Reviewers { get; } = new();
    public AgencyView? Agency { get; set; }
    public string? Error { get; set; }
}

public sealed class Snapshot
{
    public List<PrView> Prs { get; init; } = new();
    public DateTimeOffset? LastRefresh { get; init; }
    public bool Refreshing { get; init; }
    public string? Error { get; init; }
    public List<string> RepoErrors { get; init; } = new();
    public string? Me { get; init; }
    public string? MergeCheckStatus { get; init; }
    public long OrderRevision { get; init; }
    public ApprovalProviderInfo? ApprovalProvider { get; set; }
    public string? ApprovalProviderError { get; set; }
}

public sealed record MergeRecheckResult(bool Requested, string Message);
