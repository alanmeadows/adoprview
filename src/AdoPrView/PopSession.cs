namespace AdoPrView;

public sealed record PopTarget(string Key, int Id, string Title, string Org, string Project,
    string ProjectId, string Repo, string RepoId)
{
    public string Url => $"https://dev.azure.com/{Uri.EscapeDataString(Org)}/{Uri.EscapeDataString(Project)}/_git/{Uri.EscapeDataString(Repo)}/pullrequest/{Id}";
    public string Label => $"{Repo} !{Id} - {Title}";

    public static PopTarget FromPr(PrView pr)
    {
        if (pr.Id <= 0 || !pr.IsActive || pr.IsHidden || new[] { pr.Key, pr.Org, pr.Project, pr.ProjectId, pr.Repo, pr.RepoId }.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("This PR is no longer available for the inline PoP pane. Refresh the dashboard.");
        return new(pr.Key, pr.Id, pr.Title, pr.Org, pr.Project, pr.ProjectId, pr.Repo, pr.RepoId);
    }

    public static bool AllowsNavigation(string url, bool allowBlank = false) =>
        allowBlank && url == "about:blank" ||
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.IsDefaultPort &&
        uri.UserInfo.Length == 0 && !uri.IsLoopback && Uri.CheckHostName(uri.Host) == UriHostNameType.Dns &&
        !uri.Host.TrimEnd('.').Equals("app.local", StringComparison.OrdinalIgnoreCase) &&
        !uri.Host.TrimEnd('.').EndsWith(".app.local", StringComparison.OrdinalIgnoreCase);

    public static string Origin(string? url) =>
        url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https"
            ? uri.GetLeftPart(UriPartial.Authority) : "(no HTTPS page loaded)";

    internal static bool IsDashboardSource(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.IsDefaultPort &&
        uri.Host == "app.local" && uri.UserInfo.Length == 0 && uri.AbsolutePath == "/index.html";
}

public sealed record PopPolicyResult(string State, string Message)
{
    public static PopPolicyResult FromPr(PrView pr)
    {
        var policies = pr.Policies.Where(p => p.Kind == "pop" && p.IsBlocking).ToArray();
        var prefix = pr.IsActive ? "" : "PR is no longer active. ";
        if (policies.Length == 0)
            return new("unavailable", prefix + "ADO returned no blocking PoP policy; no approval is inferred.");
        var statuses = string.Join(", ", policies.Select(p => p.Status + (p.IsExpired ? " (expired)" : "")).Distinct());
        return policies.All(p => p.Status is "approved" or "notApplicable" && !p.IsExpired)
            ? new("ok", prefix + "ADO reports PoP satisfied: " + statuses + ".")
            : new("pending", prefix + "PoP is not confirmed complete. ADO reports: " + statuses + ". Complete the website's human security-key/sign-in flow, then check again.");
    }
}

public sealed record PopSelection(PopTarget Target, long Revision);

public sealed class PopSession(Func<PopTarget, Task<PopPolicyResult>> refresh)
{
    readonly HashSet<string> checking = new(StringComparer.OrdinalIgnoreCase);
    long revision;
    public PopSelection? Selection { get; private set; }
    public bool IsOpen { get; private set; }
    public bool IsChecking => Selection is { } s && checking.Contains(s.Target.Key);
    public string Status { get; private set; } = "PoP has not been checked. Opening this page does not approve it.";
    public event Action? Changed;

    public PopSelection Open(PopTarget target)
    {
        Selection = new(target, ++revision);
        IsOpen = true;
        Status = "PoP has not been checked. Complete the website's human security-key/sign-in flow, then choose Check status.";
        Changed?.Invoke();
        return Selection;
    }

    public PopSelection? Close()
    {
        var captured = Selection;
        IsOpen = false;
        revision++;
        Changed?.Invoke();
        return captured;
    }

    public async Task<PopPolicyResult> CheckAsync(PopSelection captured)
    {
        if (!checking.Add(captured.Target.Key))
            throw new InvalidOperationException("This PR's PoP policy check is already running.");
        SetStatus(captured, "Reading this PR's real ADO PoP policy...");
        try
        {
            var result = await refresh(captured.Target);
            SetStatus(captured, result.Message);
            return result;
        }
        catch (Exception)
        {
            SetStatus(captured, "Could not read PoP policy. No approval is inferred; retry Check status or open the PR in Edge.");
            throw;
        }
        finally { checking.Remove(captured.Target.Key); Changed?.Invoke(); }
    }

    void SetStatus(PopSelection captured, string status)
    {
        if (IsOpen && Selection?.Revision == captured.Revision && revision == captured.Revision) Status = status;
        Changed?.Invoke();
    }
}
