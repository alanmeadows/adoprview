using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AdoPrView;

public sealed record CopilotRunRequest(
    string Key, int Id, string Title, string Org, string Project, string Repo, string RepoId,
    string SourceRef, string SourceCommit, string Prompt, string? Distro = null, string? Directory = null,
    bool PushToSource = false)
{
    public string PrKey => Key;
    public int PrId => Id;
    public string Organization => Org;
    public string Repository => Repo;
    public string? WslPath => Directory;
    public string? WslDistribution => Distro;
}

public sealed record CopilotRunState(
    string RunId, CopilotRunRequest Request, string Status, string Output, int? ExitCode, string? Error,
    string? Distribution, string? SourcePath, string? WorktreePath, string WorktreeStatus,
    DateTimeOffset StartedAt, DateTimeOffset? FinishedAt)
{
    public string Key => Request.Key;
    public int Id => Request.Id;
    public string Title => Request.Title;
    public string Repo => Request.Repo;
    public string PrKey => Request.PrKey;
    public int PrId => Request.PrId;
    public bool IsActive => Status is "preparing" or "running" or "recovering" or "cancelling";
    public string PermissionNotice => CopilotRunService.Notice(Request);
    public string Message => Error ?? (Status == "completed" ? "Copilot exited; inspect changes and validate the fix." : PermissionNotice);
    public string? Distro => Distribution;
    public string? SourceDirectory => SourcePath;
    public bool WorktreeRetained => WorktreePath is not null && WorktreeStatus != "removed-clean";
    public string? SessionId { get; init; }
    public int ResumeAttempts { get; init; }
    public string? LogPath { get; init; }
    public long OutputOffset { get; init; }
}

public sealed record CopilotProcessRequest(
    string Operation, string? Distribution, string Script, IReadOnlyList<string> Arguments, string? StandardInput = null);

public sealed record CopilotProcessResult(int ExitCode, string StandardOutput, string StandardError);

public interface ICopilotRunTransport
{
    Task<CopilotProcessResult> ExecuteAsync(CopilotProcessRequest request, Action<string>? stdout,
        Action<string>? stderr, CancellationToken cancellationToken);
}

/// <summary>Owns only recorded tasks and isolated worktrees created by this application.</summary>
public sealed partial class CopilotRunService : IAsyncDisposable
{
    // UTF-16 storage is bounded to 256 KiB per snapshot.
    public const int MaxOutputCharacters = 128 * 1024;
    public const string HeadlessNotice =
        "Inline Copilot is non-interactive: it cannot show permission or sign-in dialogs. " +
        "Each deliberate Fix PR run uses --yolo to allow tools, paths and URLs without confirmation; " +
        "global Copilot settings are unchanged. Work starts in the isolated PR worktree, but this is not a filesystem sandbox. " +
        "Existing push/merge command denies and the prompt's no-approval/policy-bypass boundaries remain. " +
        "Authenticate in WSL first if needed. " +
        "A zero exit code means the run ended, not that the PR fix was verified.";
    public const string PushNotice =
        "Inline Copilot is non-interactive: it cannot show permission or sign-in dialogs. " +
        "Each deliberate Fix PR run uses --yolo to allow tools, paths and URLs without confirmation; " +
        "global Copilot settings are unchanged. Work starts in the isolated PR worktree, but this is not a filesystem sandbox. " +
        "This run may commit validated fixes and ordinary-push them to the PR's captured source branch; git push is not denied, " +
        "so that branch-only/no-force limit is a prompt instruction, not app-enforced. " +
        "Merge and PR-update command denies and the prompt's no-approval/policy-bypass boundaries remain. " +
        "Authenticate in WSL first if needed. " +
        "A zero exit code means the run ended, not that the PR fix was verified or published.";
    public static string Notice(CopilotRunRequest request) => request.PushToSource ? PushNotice : HeadlessNotice;

    static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    /// <summary>Exact remote/ref appended to a new run's prompt after the WSL remote was verified.</summary>
    internal static string PublishInstructions(CopilotRunRequest r, string remote, string? pushHash) => r.PushToSource
        ? "\n## Verified publish target for this run\n" +
          $"Before launch, Git remote {ShellQuote(remote)} in this checkout was verified: its fetch URL and every effective push URL (git remote get-url --push --all, including pushurl and pushInsteadOf rewrites) point to this PR's repository. " +
          $"ADO reported source ref {r.SourceRef} at {r.SourceCommit.ToLowerInvariant()} when this run started. " +
          $"After every gate above passes, publish only with: [ \"$(git remote get-url --push --all {ShellQuote(remote)} | sha256sum)\" = {ShellQuote(pushHash + "  -")} ] && git push --no-follow-tags --recurse-submodules=check {ShellQuote(remote)} {ShellQuote("HEAD:" + r.SourceRef)} " +
          $"and verify with: git ls-remote {ShellQuote(remote)} {ShellQuote(r.SourceRef)} plus a fresh GET of this PR. " +
          "The bracketed check re-verifies immediately before pushing that the remote's effective push destinations are exactly the verified ones; if it fails, nothing is pushed: report publication blocked, preserve the work and do not edit Git configuration. " +
          "--no-follow-tags keeps a push.followTags setting from also publishing tags. " +
          "--recurse-submodules=check makes Git refuse the push if the pushed commits reference submodule commits that are not already published, instead of pushing other repositories; if it refuses, publication is blocked pending separate authority: keep the work, do not push any submodule and do not report completion. Because every push destination was verified to be this PR's repository, the read-back checks the intended destination. " +
          "The dashboard does not enforce this ref restriction; follow it exactly.\n"
        : "\n## Publication for this run\nThis run is not authorized to push (git push is denied). Report any commits as unpublished local work.\n";

    readonly object gate = new();
    readonly object notificationGate = new();
    readonly ICopilotRunTransport transport;
    readonly DurableStore? store;
    readonly Dictionary<string, Entry> runs = new(StringComparer.Ordinal);
    readonly HashSet<string> activePrs = new(StringComparer.OrdinalIgnoreCase);
    readonly Timer notifications;
    bool disposed, stopping;
    Task? disposal;

    public CopilotRunService(ICopilotRunTransport? transport = null, string? stateDirectory = null)
    {
        this.transport = transport ?? new WslCopilotRunTransport();
        if (stateDirectory is not null || transport is null)
            store = new DurableStore(stateDirectory ?? Path.Combine(SettingsStore.Dir, "CopilotRuns"));
        notifications = new Timer(_ => FlushNotifications(), null, 100, 100);
    }

    public event Action<CopilotRunState>? Changed;
    public IReadOnlyList<CopilotRunState> Runs => List();

    public Task<CopilotRunState> StartAsync(CopilotRunRequest request) => Task.FromResult(Start(request));

    public CopilotRunState Start(CopilotRunRequest request)
    {
        Validate(request);
        Entry entry;
        CopilotRunState initial;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed || stopping, this);
            if (activePrs.Contains(PrIdentity(request)))
                throw new InvalidOperationException("A Copilot run for this PR is already active or its stop could not be confirmed.");
            var id = Guid.NewGuid().ToString("N");
            entry = new(new(id, request, "preparing", Notice(request) + "\n", null, null,
                null, null, null, "not-created", DateTimeOffset.UtcNow, null) { SessionId = Guid.NewGuid().ToString() });
            if (store is not null)
            {
                entry.DriverLease = store.Reserve(Save(entry), PrIdentity);
            }
            activePrs.Add(PrIdentity(request));
            initial = entry.State;
            runs.Add(id, entry);
            // Schedule outside the caller's UI turn; even fake transports may complete synchronously.
            entry.Completion = Task.Run(() => ExecuteAsync(entry));
        }
        return initial;
    }

    public CopilotRunState? Get(string runId)
    {
        lock (gate) return runs.TryGetValue(runId, out var entry) ? entry.State : null;
    }

    public IReadOnlyList<CopilotRunState> List()
    {
        lock (gate) return runs.Values.Select(e => e.State).OrderByDescending(s => s.StartedAt).ToArray();
    }

    public async Task<CopilotRunState?> CancelAsync(string runId)
    {
        Entry? entry;
        lock (gate)
        {
            if (!runs.TryGetValue(runId, out entry)) return null;
            if (store is not null) store.RequestCancel(runId);
            if (entry.State.IsActive)
            {
                Update(entry, s => s with { Status = "cancelling" });
                entry.Cancellation.Cancel();
            }
        }
        await entry.Completion.ConfigureAwait(false);
        if (store is not null && entry.State.Status == "needs-attention")
        {
            entry.DriverLease ??= store.TryLease(entry.State.RunId);
            if (entry.DriverLease is null)
                throw new InvalidOperationException("Another dashboard is controlling this run; its reservation cannot be released yet.");
            try
            {
                var latest = store.Load().Select(v => v.Saved).SingleOrDefault(v => v?.State.RunId == runId)
                    ?? throw new RunSetupException("The saved run record is unreadable. Its reservation and work are preserved.");
                entry.Environment = latest.Environment;
                entry.RunDirectory = latest.RunDirectory;
                entry.LaunchIntent = latest.LaunchIntent;
                entry.TaskIdentity = latest.TaskIdentity;
                Update(entry, s => latest.State with { Output = s.Output });
                if (latest.State.Status is "completed" or "cancelled" or "failed")
                {
                    lock (gate) activePrs.Remove(PrIdentity(entry.State.Request));
                }
                else if (!latest.LaunchIntent)
                    await FinishRecoveredAsync(entry, "cancelled", null).ConfigureAwait(false);
                else
                {
                    if (entry.Environment is null) throw new RunSetupException("A verified saved WSL environment is required to cancel this task.");
                    var ownership = await VerifyRunAsync(entry).ConfigureAwait(false);
                    var task = ReadOwnedTask(entry, await Invoke("status", entry.Environment.Distribution, StatusScript,
                        [entry.Environment.Bgtask, entry.TaskName]).ConfigureAwait(false));
                    if (task.Status == "running") await StopRecoveredAsync(entry).ConfigureAwait(false);
                    else if (task.Status is not ("exited" or "dead") || ownership[1] != "FREE")
                        throw new RunSetupException("Run liveness is uncertain; cancellation cannot safely release its reservation.");
                    await FinishRecoveredAsync(entry, "cancelled", task.ExitCode).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                var message = ex is RunSetupException ? ex.Message : "Cancellation could not be verified. Work and its reservation are preserved.";
                RecoveryBlocked(entry, message);
                throw new InvalidOperationException(message);
            }
            finally { entry.DriverLease.Dispose(); entry.DriverLease = null; }
        }
        return Get(runId);
    }

    public Task<CopilotRunState?> CancelAsync(string key, string runId)
    {
        lock (gate)
            if (!runs.TryGetValue(runId, out var e) || !string.Equals(e.State.Key, key, StringComparison.Ordinal))
                throw new InvalidOperationException("The selected PR/run no longer matches this stop request.");
        return CancelAsync(runId);
    }

    public async Task<IReadOnlyList<CopilotRunState>> StopAllAsync()
    {
        Entry[] pending;
        lock (gate)
        {
            if (disposed) return runs.Values.Select(e => e.State).ToArray();
            pending = runs.Values.Where(e => e.State.IsActive || e.UncertainStop).ToArray();
            foreach (var e in pending.Where(e => e.State.IsActive)) e.Cancellation.Cancel();
        }
        await Task.WhenAll(pending.Select(e => e.Completion)).ConfigureAwait(false);
        foreach (var e in pending.Where(e => e.UncertainStop))
        {
            try
            {
                var env = e.Environment!;
                var stopped = await Invoke("stop", env.Distribution, StopScript, [env.Bgtask, e.TaskName]).ConfigureAwait(false);
                RequireSuccess(stopped, "The owned task stop could not be confirmed.");
                var status = await Invoke("status", env.Distribution, StatusScript, [env.Bgtask, e.TaskName]).ConfigureAwait(false);
                var code = ReadExitCode(status);
                lock (gate)
                {
                    e.UncertainStop = false;
                    e.TaskConfirmedStopped = true;
                    e.State = e.State with
                    {
                        Status = "cancelled", ExitCode = code, Error = "Owned task stopped after retry. Its worktree is retained.",
                        FinishedAt = DateTimeOffset.UtcNow
                    };
                    activePrs.Remove(PrIdentity(e.State.Request));
                    e.Dirty = true;
                }
            }
            catch { /* Retain the reservation and refuse successful application shutdown. */ }
        }
        FlushNotifications();
        lock (gate)
            if (runs.Values.Any(e => e.UncertainStop))
                throw new InvalidOperationException("An owned Copilot task could not be confirmed stopped. Keep the app open, inspect its run diagnostic and retry shutdown.");
        return List();
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposed) return ValueTask.CompletedTask;
            stopping = true;
            if (disposal is null || disposal.IsFaulted) disposal = DisposeCoreAsync();
            return new(disposal);
        }
    }

    async Task DisposeCoreAsync()
    {
        Entry[] pending;
        lock (gate)
        {
            pending = runs.Values.ToArray();
            foreach (var e in pending)
            {
                e.Detaching = true;
                e.DetachCancellation.Cancel();
            }
        }
        await Task.WhenAll(pending.Select(e => e.Completion)).ConfigureAwait(false);
        await notifications.DisposeAsync().ConfigureAwait(false);
        lock (gate)
        {
            disposed = true;
            foreach (var e in runs.Values)
            {
                e.DriverLease?.Dispose();
                e.Cancellation.Dispose();
                e.DetachCancellation.Dispose();
            }
        }
    }

    static string PrIdentity(CopilotRunRequest r) =>
        string.Join("\0", r.Organization, r.Project, r.Repository, r.PrId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    static void Validate(CopilotRunRequest r)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (r.PrId <= 0 || string.IsNullOrWhiteSpace(r.PrKey) || string.IsNullOrWhiteSpace(r.Organization) ||
            string.IsNullOrWhiteSpace(r.Project) || string.IsNullOrWhiteSpace(r.Repository) || string.IsNullOrWhiteSpace(r.Prompt))
            throw new InvalidOperationException("The selected PR identity and fix prompt are required.");
        if (r.Prompt.Length > 1024 * 1024 || r.Prompt.Contains('\0'))
            throw new InvalidOperationException("The fix prompt is empty, contains NUL, or exceeds 1 MiB.");
        if (!Regex.IsMatch(r.SourceCommit ?? "", @"\A(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})\z") ||
            !(r.SourceRef ?? "").StartsWith("refs/heads/", StringComparison.Ordinal))
            throw new InvalidOperationException("A fresh PR source branch ref and exact source commit are required before launching.");
        foreach (var value in new[] { r.PrKey, r.Organization, r.Project, r.Repository, r.SourceRef, r.WslPath, r.WslDistribution })
            if (value is not null && (value.Contains('\0') || value.Any(c => c is '\r' or '\n')))
                throw new InvalidOperationException("PR identity and WSL settings cannot contain control characters.");
        if (string.IsNullOrWhiteSpace(r.WslPath) && !Regex.IsMatch(r.Repository, @"\A[a-zA-Z0-9][a-zA-Z0-9._-]*\z"))
            throw new InvalidOperationException("Configure an explicit WSL path for this repository.");
        if (!string.IsNullOrWhiteSpace(r.WslPath) && !(r.WslPath.StartsWith('/') || r.WslPath == "~" || r.WslPath.StartsWith("~/", StringComparison.Ordinal)))
            throw new InvalidOperationException("The WSL repository path must be absolute or start with ~/.");
    }

    async Task ExecuteAsync(Entry e)
    {
        var r = e.State.Request;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(e.Cancellation.Token, e.DetachCancellation.Token);
        var token = lifetime.Token;
        EnvironmentInfo? env = null;
        var launchAttempted = false;
        var finalStatus = "failed";
        string? error = null;
        int? exitCode = null;
        try
        {
            var probe = await Invoke("preflight", r.WslDistribution, PreflightScript,
                [r.WslPath ?? "", r.Repository], token: token).ConfigureAwait(false);
            RequireSuccess(probe, "WSL preflight failed. Check the configured distro/path, Git, Copilot CLI and bgtask installation.");
            env = ParseEnvironment(probe.StandardOutput, r);
            e.Environment = env;
            e.RunDirectory = env.Home + "/.local/state/ado-prview/runs/" + e.State.RunId;
            Update(e, s => s with { Distribution = env.Distribution, SourcePath = env.Root });
            CheckCli(env.Help);
            token.ThrowIfCancellationRequested();
            var prepared = await Invoke("prepare", env.Distribution, PrepareScript,
                [env.Root, env.CommonDirectory, env.Remote, env.RemoteHash, r.SourceRef, r.SourceCommit.ToLowerInvariant(),
                    e.State.RunId, r.PrId.ToString(System.Globalization.CultureInfo.InvariantCulture), e.OwnershipToken, e.State.SessionId!,
                    r.PushToSource ? env.PushHash! : ""],
                token: CancellationToken.None).ConfigureAwait(false);
            // Prepare is intentionally not interrupted halfway through Git metadata updates.
            var fields = ProtocolFields(prepared.StandardOutput);
            if (fields.Length >= 3 && fields[0] == "WORKTREE")
            {
                e.RunDirectory = fields[1];
                Update(e, s => s with { WorktreePath = fields[2], WorktreeStatus = "created" });
            }
            RequireSuccess(prepared, "Could not create the isolated worktree. Verify Git authentication and that the fresh PR source ref still points to the selected commit.");
            if (e.RunDirectory is null)
                throw new RunSetupException("WSL returned an invalid worktree receipt; no Copilot task was launched.");
            token.ThrowIfCancellationRequested();
            var staged = await Invoke("stage-prompt", env.Distribution, StagePromptScript,
                [e.RunDirectory], r.Prompt + PublishInstructions(r, env.Remote, env.PushHash), token: token).ConfigureAwait(false);
            RequireSuccess(staged, "Could not securely stage the prompt in the app-owned run directory.");
            token.ThrowIfCancellationRequested();
            Append(e, "\nStarting isolated worktree run. Interactive approvals are not available.\n", false);
            launchAttempted = true;
            e.LaunchIntent = true;
            Persist(e);
            var launched = await Invoke("launch", env.Distribution, LaunchScript,
                LaunchArguments(e, resume: false),
                token: CancellationToken.None).ConfigureAwait(false);
            RequireSuccess(launched, "The managed WSL task could not be launched; the worktree is retained.");
            Update(e, s => s with { Status = "running", WorktreeStatus = "in-use" });
            if (store is not null)
            {
                var task = ReadOwnedTask(e, await Invoke("status", env.Distribution, StatusScript, [env.Bgtask, e.TaskName],
                    token: token).ConfigureAwait(false));
                e.TaskIdentity = task.Id;
                Update(e, s => s with { LogPath = task.Log });
            }
            token.ThrowIfCancellationRequested();
            var followed = await Invoke("follow", env.Distribution, FollowScript, [env.Bgtask, e.TaskName],
                stdout: text => Append(e, text, false), stderr: text => Append(e, text, true), token: token).ConfigureAwait(false);
            RequireSuccess(followed, "Live output could not be followed. The supervised task was not cancelled; its worktree and reservation are retained.");
            var status = await Invoke("status", env.Distribution, StatusScript, [env.Bgtask, e.TaskName],
                token: CancellationToken.None).ConfigureAwait(false);
            exitCode = ReadExitCode(status);
            e.TaskConfirmedStopped = true;
            finalStatus = exitCode == 0 ? "completed" : "failed";
            if (store?.IsCancelled(e.State.RunId) == true) finalStatus = "cancelled";
            if (exitCode != 0) error = $"Copilot exited with code {exitCode}. Inspect the output; no verified fix is claimed.";
        }
        catch (OperationCanceledException) when (e.Detaching)
        {
            error = "Dashboard detached; the supervised job is not cancelled. Startup will reconcile its actual state.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            finalStatus = "cancelled";
            error = "Run cancelled. Any created worktree is retained for inspection.";
        }
        catch (Exception ex)
        {
            // Never forward process exceptions, stderr, URLs, environment or argv into UI diagnostics.
            error = ex is RunSetupException ? ex.Message : "The WSL run failed. Check WSL availability and the configured CLI tools.";
        }
        finally
        {
            if (e.Detaching)
            {
                Persist(e);
            }
            else
            {
            if (launchAttempted && !e.TaskConfirmedStopped && env is not null && e.Cancellation.IsCancellationRequested)
            {
                try
                {
                    if (store is not null) await StopRecoveredAsync(e).ConfigureAwait(false);
                    else
                    {
                    var stopped = await Invoke("stop", env.Distribution, StopScript, [env.Bgtask, e.TaskName],
                        token: CancellationToken.None).ConfigureAwait(false);
                    RequireSuccess(stopped, "The owned task stop could not be confirmed.");
                    }
                    var status = await Invoke("status", env.Distribution, StatusScript, [env.Bgtask, e.TaskName],
                        token: CancellationToken.None).ConfigureAwait(false);
                    exitCode = ReadExitCode(status);
                    e.TaskConfirmedStopped = true;
                }
                catch
                {
                    e.UncertainStop = true;
                    finalStatus = "failed";
                    error = $"Could not confirm the owned task stopped. In the configured WSL distro run `bgtask status {e.TaskName}`; " +
                        "its worktree and same-PR reservation are retained. No unrelated process was stopped.";
                }
            }
            if (launchAttempted && !e.TaskConfirmedStopped)
            {
                e.UncertainStop = true;
                finalStatus = "needs-attention";
                error ??= "Task liveness is uncertain; its worktree and PR reservation are preserved for startup reconciliation.";
            }
            Update(e, s => s with { Status = finalStatus, ExitCode = exitCode, Error = error, FinishedAt = DateTimeOffset.UtcNow });
            // Terminal completion is durable before cleanup; a cleanup failure must never become a resumable run.
            if (env is not null && e.RunDirectory is not null)
            {
                try
                {
                    // This removes only the private prompt, not the worktree or user files.
                    await Invoke("discard-prompt", env.Distribution, DiscardPromptScript, [e.RunDirectory],
                        token: CancellationToken.None).ConfigureAwait(false);
                    if (finalStatus == "completed" && e.TaskConfirmedStopped)
                    {
                        var cleaned = await Invoke("cleanup", env.Distribution, CleanupScript, CleanupArguments(e),
                            token: CancellationToken.None).ConfigureAwait(false);
                        var disposition = ProtocolFields(cleaned.StandardOutput);
                        Update(e, s => s with
                        {
                            WorktreeStatus = cleaned.ExitCode == 0 && disposition.FirstOrDefault() == "REMOVED"
                                ? "removed-clean" : "retained-changes",
                            Error = disposition.FirstOrDefault() == "REMOVED" ? null :
                                "Completed; worktree retained: " + (disposition.ElementAtOrDefault(1) ?? "safe ownership/liveness/publication could not be verified.")
                        });
                    }
                    else Update(e, s => s with { WorktreeStatus = "retained" });
                }
                catch { Update(e, s => s with { WorktreeStatus = "retained",
                    Error = finalStatus == "completed" ? "Completed; cleanup failed. Worktree retained; completion is not resumable." : s.Error }); }
            }
            lock (gate)
            {
                e.Dirty = true;
                if (!e.UncertainStop) activePrs.Remove(PrIdentity(r));
            }
            FlushNotifications();
            e.DriverLease?.Dispose();
            e.DriverLease = null;
            }
        }
    }

    Task<CopilotProcessResult> Invoke(string operation, string? distribution, string script, IReadOnlyList<string> arguments,
        string? input = null, Action<string>? stdout = null, Action<string>? stderr = null, CancellationToken token = default) =>
        transport.ExecuteAsync(new(operation, string.IsNullOrWhiteSpace(distribution) ? null : distribution,
            EnvironmentScript + "\n" + script, arguments, input), stdout, stderr, token);

    static void RequireSuccess(CopilotProcessResult result, string message)
    {
        if (result.ExitCode != 0) throw new RunSetupException(message);
    }

    static string[] ProtocolFields(string output)
    {
        const string marker = "ADOPRVIEW1\0";
        var start = output.IndexOf(marker, StringComparison.Ordinal);
        return start < 0 ? [] : output[(start + marker.Length)..].TrimEnd('\0').Split('\0');
    }

    static EnvironmentInfo ParseEnvironment(string output, CopilotRunRequest request)
    {
        var f = ProtocolFields(output);
        if (f.Length < 9 || f.Take(7).Any(string.IsNullOrWhiteSpace))
            throw new RunSetupException("WSL preflight returned invalid environment data.");
        var fetchMatched = false;
        for (var i = 9; i + 3 < f.Length; i += 4)
        {
            if (!RemoteMatches(f[i + 1], request.Organization, request.Project, request.Repository)) continue;
            fetchMatched = true;
            // Only push-authorized runs depend on where pushes go; every effective push URL must be this repository.
            if (request.PushToSource && (!Regex.IsMatch(f[i + 3], @"\A[0-9a-f]{64}\z") ||
                !f[i + 2].Split('\n').All(u => RemoteMatches(u, request.Organization, request.Project, request.Repository))))
                continue;
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(f[i + 1]))).ToLowerInvariant();
            return new(f[0], f[3], f[4], f[5], f[6], f[7], f[i], hash, f[2], f[8], f[i + 3]);
        }
        throw new RunSetupException(fetchMatched
            ? "This run is authorized to publish, but no Git remote in the configured WSL checkout has a fetch URL and every effective push URL (including pushurl and pushInsteadOf rewrites) matching this PR's exact organization, project and repository. Nothing was pushed, Git configuration was not changed, and no worktree or Copilot task was created."
            : "The configured WSL checkout has no Git remote matching this PR's exact organization, project and repository. No worktree or Copilot task was created.");
    }

    internal static bool RemoteMatches(string remote, string org, string project, string repo)
    {
        string[] parts;
        if (remote.StartsWith("git@ssh.dev.azure.com:", StringComparison.OrdinalIgnoreCase))
        {
            parts = remote["git@ssh.dev.azure.com:".Length..].Split('/');
            return parts.Length == 4 && parts[0] == "v3" && Equal(parts[1], org) && Equal(parts[2], project) && EqualRepo(parts[3], repo);
        }
        if (!Uri.TryCreate(remote, UriKind.Absolute, out var uri) || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return false;
        parts = uri.AbsolutePath.Trim('/').Split('/');
        if (uri.Scheme == "ssh" && uri.Host.Equals("ssh.dev.azure.com", StringComparison.OrdinalIgnoreCase))
            return parts.Length == 4 && parts[0] == "v3" && Equal(parts[1], org) && Equal(parts[2], project) && EqualRepo(parts[3], repo);
        if (uri.Scheme != "https" || !uri.IsDefaultPort) return false;
        if (uri.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase))
            return parts.Length == 4 && Equal(parts[0], org) && Equal(parts[1], project) && parts[2] == "_git" && EqualRepo(parts[3], repo);
        if (uri.Host.Equals(org + ".visualstudio.com", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length == 4 && parts[0].Equals("DefaultCollection", StringComparison.OrdinalIgnoreCase)) parts = parts[1..];
            return parts.Length == 3 && Equal(parts[0], project) && parts[1] == "_git" && EqualRepo(parts[2], repo);
        }
        return false;
        static bool Equal(string encoded, string expected) =>
            Uri.UnescapeDataString(encoded).Equals(expected, StringComparison.OrdinalIgnoreCase);
        static bool EqualRepo(string encoded, string expected) => Equal(encoded, expected);
    }

    static void CheckCli(string help)
    {
        var required = new[] { "--no-ask-user", "--stream", "--no-color", "--deny-tool", "--yolo", "--session-id", "--resume" };
        var missing = required.Where(option =>
            !Regex.IsMatch(help, @"(?<![\w-])" + Regex.Escape(option) + @"(?![\w-])", RegexOptions.CultureInvariant)).ToArray();
        if (missing.Length != 0)
            throw new RunSetupException("The installed WSL Copilot CLI does not advertise required headless inline-run options: " +
                string.Join(", ", missing) + ". Check 'copilot --help' in the configured WSL distribution and update that CLI if needed.");
    }

    static int ReadExitCode(CopilotProcessResult result)
    {
        RequireSuccess(result, "Could not read the owned managed task status.");
        try
        {
            using var json = JsonDocument.Parse(result.StandardOutput);
            var status = json.RootElement.GetProperty("status");
            if (status.GetProperty("state").GetString() == "exited")
                return status.GetProperty("exited").GetProperty("code").GetInt32();
        }
        catch (JsonException) { }
        catch (KeyNotFoundException) { }
        throw new RunSetupException("The managed task did not report a real exit code; no completion is claimed.");
    }

    void Update(Entry e, Func<CopilotRunState, CopilotRunState> update)
    {
        lock (gate) { e.State = update(e.State); e.Dirty = true; Persist(e); }
    }

    void Append(Entry e, string text, bool stderr)
    {
        lock (gate)
        {
            var clean = (stderr ? e.StderrFilter : e.StdoutFilter).Filter(text);
            if (clean.Length == 0) return;
            var output = e.State.Output + clean;
            if (output.Length > MaxOutputCharacters)
            {
                const string prefix = "[Earlier output truncated]\n";
                var start = output.Length - MaxOutputCharacters + prefix.Length;
                if (start < output.Length && char.IsLowSurrogate(output[start])) start++;
                output = prefix + output[start..];
            }
            e.State = e.State with { Output = output, OutputOffset = e.State.OutputOffset + clean.Length };
            e.Dirty = true;
        }
    }

    void FlushNotifications()
    {
        lock (notificationGate)
        {
            CopilotRunState[] changed;
            lock (gate)
            {
                changed = runs.Values.Where(e => e.Dirty).Select(e => { e.Dirty = false; return e.State; }).ToArray();
            }
            foreach (var state in changed)
                if (Changed is { } handlers)
                    foreach (Action<CopilotRunState> handler in handlers.GetInvocationList())
                        try { handler(state); } catch { /* A UI subscriber cannot orphan an owned task. */ }
        }
    }

    sealed class Entry(CopilotRunState state)
    {
        public CopilotRunState State = state;
        public readonly CancellationTokenSource Cancellation = new();
        public readonly CancellationTokenSource DetachCancellation = new();
        public readonly TerminalFilter StdoutFilter = new(), StderrFilter = new();
        public Task Completion = Task.CompletedTask;
        public string TaskName => "ado-prview-" + State.RunId;
        public string? RunDirectory;
        public EnvironmentInfo? Environment;
        public bool TaskConfirmedStopped, UncertainStop, Dirty = true;
        public bool Detaching, LaunchIntent;
        public string OwnershipToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        public FileStream? DriverLease;
        public string? TaskIdentity;
        public readonly TaskCompletionSource Reconciled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed record EnvironmentInfo(string Distribution, string Root, string CommonDirectory, string Copilot,
        string Bgtask, string Help, string Remote, string RemoteHash, string Home, string CopilotHome, string? PushHash = null);
    sealed class RunSetupException(string message) : Exception(message);

    sealed class TerminalFilter
    {
        int state;
        public string Filter(string value)
        {
            var b = new StringBuilder(value.Length);
            foreach (var c in value)
            {
                switch (state)
                {
                    case 1: state = c == '[' ? 2 : c is ']' or 'P' or '^' or '_' ? 3 : 0; break;
                    case 2: if (c is >= '@' and <= '~') state = 0; break;
                    case 3: if (c is '\a' or '\u009c') state = 0; else if (c == '\u001b') state = 4; break;
                    case 4: state = c == '\\' ? 0 : 3; break;
                    default:
                        if (c == '\u001b') state = 1;
                        else if (c == '\u009b') state = 2;
                        else if (c is '\u009d' or '\u0090') state = 3;
                        else if (c == '\r') b.Append('\n');
                        else if (c is '\n' or '\t' || !char.IsControl(c)) b.Append(c);
                        break;
                }
            }
            return b.ToString();
        }
    }

    internal const string EnvironmentScript = """
        set -euo pipefail
        # Source only the current Linux user's regular environment files; never print their values.
        inherited_env="${ADO_PRVIEW_BASH_ENV-}"
        envfile="$HOME/.config/bash/environment"
        if [[ -f "$envfile" && -O "$envfile" && ! -L "$envfile" ]]; then source "$envfile" >/dev/null; fi
        requested_env="${inherited_env:-${BASH_ENV-}}"
        if [[ -n "$requested_env" && "$requested_env" != "$envfile" ]]; then
          [[ -f "$requested_env" && -O "$requested_env" && ! -L "$requested_env" ]] || exit 71
          source "$requested_env" >/dev/null
        fi
        if [[ -n "$requested_env" ]]; then
          [[ -f "$requested_env" && -O "$requested_env" && ! -L "$requested_env" ]] || exit 71
          export BASH_ENV="$requested_env"
        fi
        if ! command -v copilot >/dev/null 2>&1 && [[ -f "$HOME/.nvm/nvm.sh" && -O "$HOME/.nvm/nvm.sh" && ! -L "$HOME/.nvm/nvm.sh" ]]; then
          source "$HOME/.nvm/nvm.sh" >/dev/null
          nvm use default --silent >/dev/null
        fi
        export NO_COLOR=1 TERM=dumb GIT_TERMINAL_PROMPT=0 GCM_INTERACTIVE=never
        """;

    internal const string PreflightScript = """
        cwd="$1"; repo="$2"
        if [[ -z "$cwd" ]]; then cwd="$HOME/clones/$repo"; fi
        if [[ "$cwd" == "~" ]]; then cwd="$HOME"; elif [[ "$cwd" == "~/"* ]]; then cwd="$HOME/${cwd:2}"; fi
        [[ "$cwd" == /* && -d "$cwd" ]] || exit 72
        cd -- "$cwd"
        root="$(git rev-parse --show-toplevel 2>/dev/null)"
        cd -- "$root"; root="$(pwd -P)"
        common="$(git rev-parse --git-common-dir)"
        common="$(cd -- "$common" && pwd -P)"
        cli="$(command -v copilot)"
        bg="$(command -v bgtask || true)"
        if [[ -z "$bg" && -x "$HOME/go/bin/bgtask" ]]; then bg="$HOME/go/bin/bgtask"; fi
        [[ "$cli" == /* && -x "$cli" && "$bg" == /* && -x "$bg" ]] || exit 73
        command -v flock setsid python3 >/dev/null
        help="$("$cli" --help)"
        "$bg" --help >/dev/null
        copilot_home="${COPILOT_HOME:-$HOME/.copilot}"
        [[ "$copilot_home" == /* ]] || exit 73
        printf 'ADOPRVIEW1\0%s\0%s\0%s\0%s\0%s\0%s\0%s\0%s\0%s\0' "${WSL_DISTRO_NAME:?}" "$(id -un)" "$HOME" "$root" "$common" "$cli" "$bg" "$help" "$copilot_home"
        while IFS= read -r remote; do
          url="$(git remote get-url "$remote")"
          push="$(git remote get-url --push --all "$remote")"
          pushhash="$(git remote get-url --push --all "$remote" | sha256sum)"
          printf '%s\0%s\0%s\0%s\0' "$remote" "$url" "$push" "${pushhash%% *}"
        done < <(git remote)
        """;

    internal const string PrepareScript = """
        root="$1"; common="$2"; remote="$3"; remotehash="$4"; sourceref="$5"; commit="$6"; id="$7"; prid="$8"; nonce="$9"; session="${10}"; pushhash="${11-}"
        [[ "$id" =~ ^[0-9a-f]{32}$ && "$commit" =~ ^([0-9a-f]{40}|[0-9a-f]{64})$ ]] || exit 74
        cd -- "$root"
        [[ "$(cd -- "$(git rev-parse --git-common-dir)" && pwd -P)" == "$common" ]] || exit 74
        git check-ref-format "$sourceref" >/dev/null
        [[ "$sourceref" == refs/heads/* ]] || exit 74
        actual="$(git remote get-url "$remote")"; actual="$(printf %s "$actual" | sha256sum)"; actual="${actual%% *}"
        [[ "$actual" == "$remotehash" ]] || exit 74
        if [[ -n "$pushhash" ]]; then
          actual="$(git remote get-url --push --all "$remote" | sha256sum)"
          [[ "${actual%% *}" == "$pushhash" ]] || exit 74
        fi
        base="$HOME/.local/state/ado-prview/runs"
        mkdir -p -- "$base"
        [[ -O "$base" && ! -L "$base" ]] || exit 74
        run="$base/$id"; mkdir -m 700 -- "$run"
        worktree="$run/worktree"; branch="ado-prview/pr-$prid-$id"; ref="refs/ado-prview/$id/source"
        printf 'ADOPRVIEW1\0WORKTREE\0%s\0%s\0' "$run" "$worktree"
        exec 9>"$common/ado-prview.metadata.lock"
        flock -w 60 9
        git -c core.hooksPath=/dev/null fetch --quiet --no-tags --no-recurse-submodules --no-write-fetch-head -- "$remote" "$sourceref:$ref" 2>/dev/null
        [[ "$(git rev-parse "$ref^{commit}")" == "$commit" ]] || exit 75
        git -c core.hooksPath=/dev/null worktree add --quiet --no-track -b "$branch" -- "$worktree" "$commit" 2>/dev/null
        printf '%s\n%s\n%s\n%s\n%s\n' "$root" "$branch" "$commit" "$nonce" "$session" > "$run/ownership"
        chmod 600 "$run/ownership"
        """;

    internal const string StagePromptScript = """
        run="$1"
        [[ -d "$run" && -O "$run" && ! -L "$run" && -f "$run/ownership" ]] || exit 76
        umask 077
        set -o noclobber
        cat > "$run/prompt"
        """;

    internal const string RunnerScript = """
        set -euo pipefail
        cli="$1"; run="$2"; session="$3"; nonce="$4"; mode="$5"
        # Only *-push modes (new deliberate push-authorized launches) omit the git push deny.
        denies=(--deny-tool 'shell(git push)' --deny-tool 'shell(gh pr merge)' --deny-tool 'shell(az repos pr update)')
        if [[ "$mode" == *-push ]]; then denies=(--deny-tool 'shell(gh pr merge)' --deny-tool 'shell(az repos pr update)'); fi
        [[ -f "$run/ownership" && -O "$run/ownership" && ! -L "$run/ownership" ]] || exit 76
        mapfile -t receipt < "$run/ownership"
        [[ "${receipt[3]-}" == "$nonce" && "${receipt[4]-}" == "$session" ]] || exit 76
        exec 8>"$run/task.lock"; flock -n 8 || exit 78
        if [[ "$mode" == start || "$mode" == start-push ]]; then
          [[ -f "$run/prompt" && -O "$run/prompt" && ! -L "$run/prompt" ]] || exit 76
          exec 3< "$run/prompt"; rm -- "$run/prompt"
          sessionargs=(--session-id "$session")
        elif [[ "$mode" == resume || "$mode" == resume-push ]]; then
          exec 3</dev/null
          sessionargs=(--resume "$session" --prompt "Continue the interrupted task in this saved session from where it stopped; preserve its original instructions and scope. Do not start a new task.")
        else exit 76; fi
        unset COPILOT_ALLOW_ALL
        export COPILOT_ASSISTED_APPROVAL=false NO_COLOR=1 TERM=dumb
        set +e
        child=""; interrupted=0
        stop_cli() {
          interrupted=143
          if [[ -n "$child" ]]; then kill -TERM -- "-$child" 2>/dev/null || true; fi
        }
        trap stop_cli TERM INT
        setsid "$cli" --stream on --no-color --no-ask-user --yolo "${sessionargs[@]}" "${denies[@]}" <&3 &
        child=$!
        wait "$child"; code=$?
        if [[ "$interrupted" != 0 ]]; then wait "$child"; code="$interrupted"; fi
        trap - TERM INT
        umask 077
        printf '%s\n%s\n%s\n' "$nonce" "$session" "$code" > "$run/outcome.tmp"
        mv -- "$run/outcome.tmp" "$run/outcome"
        exit "$code"
        """;

    internal const string LaunchScript = """
        bg="$1"; name="$2"; worktree="$3"; cli="$4"; run="$5"; runner="$6"; session="$7"; nonce="$8"; mode="$9"
        [[ -d "$worktree" && -f "$run/ownership" ]] || exit 76
        [[ ! -L "$run/launch.lock" && ! -L "$run/task.lock" ]] || exit 76
        exec 7>"$run/launch.lock"; flock -n 7 || exit 78
        exec 8>"$run/task.lock"; flock -n 8 || exit 78
        flock -u 8
        "$bg" run --name "$name" --dir "$worktree" -- bash --noprofile --norc -c "$runner" ado-prview-runner "$cli" "$run" "$session" "$nonce" "$mode" 7>&- 8>&-
        """;
    internal const string FollowScript = "\"$1\" logs \"$2\" --follow";
    internal const string StatusScript = "\"$1\" status \"$2\" --json";
    internal const string StopScript = "\"$1\" stop \"$2\" --timeout 3s";
    internal const string DiscardPromptScript = """
        run="$1"
        if [[ -d "$run" && -O "$run" && ! -L "$run" && -f "$run/prompt" && ! -L "$run/prompt" ]]; then rm -- "$run/prompt"; fi
        """;
    internal const string CleanupScript = """
        root="$1"; common="$2"; run="$3"; id="$4"; commit="$5"; nonce="$6"; session="$7"; bg="$8"; name="$9"; remote="${10}"; remotehash="${11}"; sourceref="${12}"
        [[ "$run" == "$HOME/.local/state/ado-prview/runs/$id" && "$id" =~ ^[0-9a-f]{32}$ &&
           -f "$run/ownership" && -O "$run/ownership" && ! -L "$run/ownership" && ! -L "$run" ]] || exit 77
        mapfile -t receipt < "$run/ownership"
        [[ "${receipt[0]}" == "$root" && "${receipt[2]}" == "$commit" && "${receipt[1]}" == ado-prview/pr-*-"$id" &&
           "${receipt[3]-}" == "$nonce" && "${receipt[4]-}" == "$session" ]] || exit 77
        branch="${receipt[1]}"; worktree="$run/worktree"; ref="refs/ado-prview/$id/source"
        [[ -d "$worktree" && ! -L "$worktree" ]] || exit 77
        cd -- "$root"
        [[ "$(cd -- "$(git rev-parse --git-common-dir)" && pwd -P)" == "$common" ]] || exit 77
        exec 9>"$common/ado-prview.metadata.lock"; flock -w 60 9
        [[ ! -L "$run/task.lock" ]] || exit 77
        exec 8>"$run/task.lock"; flock -n 8 || { printf 'ADOPRVIEW1\0RETAINED\0execution lock is held\0'; exit 0; }
        task="$("$bg" status "$name" --json)" || exit 77
        printf '%s' "$task" | python3 -c 'import json,sys; t=json.load(sys.stdin); a=t["command"]; s=t["status"]; assert t["cwd"]==sys.argv[1] and t["name"]==sys.argv[2] and a[-3]==sys.argv[3] and a[-2]==sys.argv[4] and s["state"]=="exited" and s["exited"]["code"]==0' "$worktree" "$name" "$session" "$nonce" || exit 77
        [[ "$(git -C "$worktree" rev-parse --show-toplevel)" == "$worktree" ]] || exit 77
        [[ "$(git -C "$worktree" rev-parse --path-format=absolute --git-common-dir)" == "$common" ]] || exit 77
        [[ "$(git -C "$worktree" symbolic-ref HEAD)" == "refs/heads/$branch" ]] || exit 77
        [[ -z "$(git -C "$worktree" status --porcelain --untracked-files=all --ignored=matching)" ]] || { printf 'ADOPRVIEW1\0RETAINED\0uncommitted, untracked or ignored files remain\0'; exit 0; }
        head="$(git -C "$worktree" rev-parse HEAD)"
        publishedref="refs/ado-prview/$id/published"
        if [[ "$head" != "$commit" ]]; then
          actual="$(git remote get-url "$remote")"; actual="$(printf %s "$actual" | sha256sum)"; actual="${actual%% *}"
          [[ "$actual" == "$remotehash" && "$sourceref" == refs/heads/* ]] || exit 77
          git -c core.hooksPath=/dev/null fetch --quiet --no-tags --no-recurse-submodules --no-write-fetch-head -- "$remote" "$sourceref:$publishedref" 2>/dev/null ||
            { printf 'ADOPRVIEW1\0RETAINED\0publication could not be verified\0'; exit 0; }
          git merge-base --is-ancestor "$head" "$publishedref" ||
            { printf 'ADOPRVIEW1\0RETAINED\0commits are not published in the intended PR source\0'; exit 0; }
        fi
        # Delete the owned branch and refs only after Git really removed the worktree (it refuses, e.g., with initialized submodules).
        git -c core.hooksPath=/dev/null worktree remove -- "$worktree" ||
          { printf 'ADOPRVIEW1\0RETAINED\0git refused to remove the worktree (for example it has initialized submodules); it remains registered at %s\0' "$worktree"; exit 0; }
        git update-ref -d "refs/heads/$branch" "$head"
        git update-ref -d "$ref" "$commit"
        git update-ref -d "$publishedref"
        printf 'ADOPRVIEW1\0REMOVED\0'
        """;
}

public sealed class WslCopilotRunTransport : ICopilotRunTransport
{
    public async Task<CopilotProcessResult> ExecuteAsync(CopilotProcessRequest request, Action<string>? stdout,
        Action<string>? stderr, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo("wsl.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        if (request.Distribution is not null) { info.ArgumentList.Add("--distribution"); info.ArgumentList.Add(request.Distribution); }
        // POSIX sh does not auto-source BASH_ENV. Preserve its path for explicit ownership checks in bash.
        foreach (var arg in new[] { "--exec", "/bin/sh", "-c",
            "export ADO_PRVIEW_BASH_ENV=\"${BASH_ENV-}\"; unset BASH_ENV; exec \"$@\"", "ado-prview-env" })
            info.ArgumentList.Add(arg);
        if (request.Operation != "follow")
            foreach (var arg in new[] { "timeout", "--signal=TERM", "--kill-after=5s", "120s" }) info.ArgumentList.Add(arg);
        // Raw string literals inherit the Windows source file's line endings, unlike prompt stdin.
        foreach (var arg in new[] { "bash", "--noprofile", "--norc", "-c", request.Script.Replace("\r\n", "\n"), "ado-prview" })
            info.ArgumentList.Add(arg);
        foreach (var arg in request.Arguments) info.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = info };
        cancellationToken.ThrowIfCancellationRequested();
        process.Start();
        // Kill only this invocation's Windows process tree. Managed Linux tasks are stopped separately by exact unique name.
        using var registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        });
        var output = new StringBuilder();
        var errors = new StringBuilder();
        async Task Drain(StreamReader reader, StringBuilder capture, Action<string>? emit)
        {
            var buffer = new char[4096];
            int count;
            try
            {
                while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
                {
                    var text = new string(buffer, 0, count);
                    emit?.Invoke(text);
                    if (capture.Length < CopilotRunService.MaxOutputCharacters)
                        capture.Append(text.AsSpan(0, Math.Min(text.Length, CopilotRunService.MaxOutputCharacters - capture.Length)));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }
        var outTask = Drain(process.StandardOutput, output, stdout);
        var errTask = Drain(process.StandardError, errors, stderr);
        try
        {
            if (request.StandardInput is not null)
                await process.StandardInput.WriteAsync(request.StandardInput.AsMemory(), cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(outTask, errTask).ConfigureAwait(false);
            return new(process.ExitCode, output.ToString(), errors.ToString());
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(outTask, errTask).ConfigureAwait(false);
            throw;
        }
    }
}
