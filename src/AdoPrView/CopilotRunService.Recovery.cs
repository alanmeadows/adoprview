using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AdoPrView;

public sealed partial class CopilotRunService
{
    Task? recovery;

    internal sealed record SavedRun(int Version, CopilotRunState State, EnvironmentInfo? Environment,
        string? RunDirectory, string OwnershipToken, bool LaunchIntent, bool TaskConfirmedStopped, string? TaskIdentity)
    {
        public NativeFixSupervisor.Specification? Native { get; init; }
    }

    SavedRun Save(Entry e, CopilotRunState? state = null) => new(1, (state ?? e.State) with
    {
        Request = e.State.Request with { Prompt = "", AdditionalInstructions = null }, Output = ""
    }, e.Environment is null ? null : e.Environment with { Help = "" },
        e.RunDirectory, e.OwnershipToken, e.LaunchIntent, e.TaskConfirmedStopped, e.TaskIdentity) { Native = e.Native };

    void Persist(Entry e)
    {
        lock (e.PersistenceGate)
        {
            SavedRun saved;
            lock (gate) saved = Save(e);
            if (store is not null && e.DriverLease is not null) store.Write(saved);
        }
    }

    string[] LaunchArguments(Entry e, bool resume) =>
        [e.Environment!.Bgtask, e.TaskName, e.State.WorktreePath!, e.Environment.Copilot, e.RunDirectory!,
            RunnerScript.Replace("\r\n", "\n"), e.State.SessionId!, e.OwnershipToken, Mode(e, resume)];

    // Push authority comes only from the run's own persisted request; records without it stay no-push.
    static string Mode(Entry e, bool resume) => (resume ? "resume" : "start") + (e.State.Request.PushToSource ? "-push" : "");

    string[] CleanupArguments(Entry e) =>
        [e.Environment!.Root, e.Environment.CommonDirectory, e.RunDirectory!, e.State.RunId,
            e.State.Request.SourceCommit.ToLowerInvariant(), e.OwnershipToken, e.State.SessionId!, e.Environment.Bgtask,
            e.TaskName, e.Environment.Remote, e.Environment.RemoteHash, e.State.Request.SourceRef];

    public Task RecoverAsync()
    {
        lock (gate) return recovery ??= Task.Run(RecoverCoreAsync);
    }

    async Task RecoverCoreAsync()
    {
        if (store is null) return;
        var ready = new List<Task>();
        foreach (var (saved, diagnostic) in store.Load())
        {
            if (saved is null)
            {
                var id = Guid.NewGuid().ToString("N");
                var request = new CopilotRunRequest("recovery:" + id, 0, "Saved Copilot run needs attention",
                    "", "", "recovery", "", "", "", "");
                var warning = new Entry(new(id, request, "needs-attention", "", null, diagnostic, null, null, null,
                    "preserved-unknown", DateTimeOffset.UtcNow, null));
                lock (gate) runs[id] = warning;
                continue;
            }
            Entry entry;
            lock (gate)
            {
                if (runs.ContainsKey(saved.State.RunId)) continue;
                entry = new(saved.State with { Output = "[Recovered run history; supervised logs are replayed when available.]\n" })
                {
                    Environment = saved.Environment, RunDirectory = saved.RunDirectory,
                    OwnershipToken = saved.OwnershipToken, LaunchIntent = saved.LaunchIntent,
                    TaskConfirmedStopped = saved.TaskConfirmedStopped, TaskIdentity = saved.TaskIdentity, Native = saved.Native,
                    DriverLease = store.TryLease(saved.State.RunId)
                };
                runs.Add(entry.State.RunId, entry);
                if (ReservesPr(saved)) activePrs.Add(PrIdentity(entry.State.Request));
                entry.Completion = Task.Run(() => saved.State.Backend == "windows-native"
                    ? RecoverNativeAsync(entry, saved) : RecoverEntryAsync(entry, saved));
                ready.Add(entry.Reconciled.Task);
            }
        }
        FlushNotifications();
        await Task.WhenAll(ready).ConfigureAwait(false);
    }

    static bool ReservesPr(SavedRun saved) => saved.State.IsActive || saved.State.MayBeRunning || saved.State.Status == "needs-attention";

    async Task RecoverEntryAsync(Entry e, SavedRun saved)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(e.Cancellation.Token, e.DetachCancellation.Token);
        var token = lifetime.Token;
        try
        {
            if (saved.State.WorktreeStatus == "removed-clean")
            {
                await ReadHistoryAsync(e, token).ConfigureAwait(false);
                return;
            }
            if (!saved.LaunchIntent && saved.State.Status is "failed" or "cancelled")
                return;
            if (saved.LaunchIntent && !saved.TaskConfirmedStopped)
                Update(e, s => s with { MayBeRunning = true, FinishedAt = null });
            if (saved.Environment is null || saved.RunDirectory is null)
                throw new RunSetupException("Preparation was interrupted before a verified workspace/session was recorded. No original prompt was replayed; inspect this run before cancelling or starting another.");
            var old = saved.Environment;
            var probe = await Invoke("preflight", old.Distribution, PreflightScript,
                [old.Root, e.State.Request.Repo], token: token).ConfigureAwait(false);
            RequirePreflightSuccess(probe);
            var env = ParseEnvironment(probe.StandardOutput, e.State.Request);
            if (env.Distribution != old.Distribution || env.Root != old.Root || env.CommonDirectory != old.CommonDirectory ||
                env.Home != old.Home || env.CopilotHome != old.CopilotHome || env.RemoteHash != old.RemoteHash ||
                old.XdgConfigHome is not null && (env.XdgConfigHome != old.XdgConfigHome || env.BgtaskStateDirectory != old.BgtaskStateDirectory))
                throw new RunSetupException("The saved WSL/repository/session environment changed. Work is preserved; no automatic resume or cleanup was attempted.");
            e.Environment = env;
            var ownership = await VerifyRunAsync(e, token).ConfigureAwait(false);
            if (ownership[0] == "REMOVED" && saved.State.Status == "completed")
            {
                Update(e, s => s with { WorktreeStatus = "removed-clean" });
                await ReadHistoryAsync(e, token).ConfigureAwait(false);
                return;
            }
            if (ownership[0] != "OWNED")
                throw new RunSetupException("The saved worktree is missing. No session was resumed in a different directory.");
            if (e.State.WorktreePath is null)
                Update(e, s => s with { WorktreePath = e.RunDirectory + "/worktree", WorktreeStatus = "retained-recovery" });
            if (!saved.LaunchIntent)
                throw new RunSetupException("Launch was never confirmed and no saved session can safely be resumed. The prepared worktree is preserved; the original prompt is not replayed.");
            var result = await InvokeRun("status", env, StatusScript, [env.Bgtask, e.TaskName], token: token).ConfigureAwait(false);
            var task = ReadOwnedTask(e, result);
            e.TaskIdentity = task.Id;
            Update(e, s => s with { LogPath = task.Log });
            if (task.Status == "running")
            {
                if (saved.State.Status is "completed" or "cancelled")
                    throw new RunSetupException("A terminal record unexpectedly has a live task. No resume or cleanup was attempted; inspect the retained worktree.");
                if (store!.IsCancelled(e.State.RunId) && e.DriverLease is not null)
                {
                    var code = await StopRecoveredAsync(e).ConfigureAwait(false);
                    await FinishRecoveredAsync(e, "cancelled", code).ConfigureAwait(false);
                    return;
                }
                Update(e, s => s with { Status = "running", Error = null, FinishedAt = null, MayBeRunning = true });
                e.Reconciled.TrySetResult();
                await FollowRecoveredAsync(e, token).ConfigureAwait(false);
                return;
            }
            if (ownership[1] != "FREE")
                throw new RunSetupException("The recorded supervisor is inactive but its workspace execution lock is still held. Liveness is uncertain; no resume or cleanup was attempted.");
            var outcome = ownership.Length > 3 && int.TryParse(ownership[3], out var recordedExit) ? recordedExit : task.ExitCode;
            if (saved.State.Status is "completed" or "cancelled" or "failed" ||
                store!.IsCancelled(e.State.RunId) || outcome == 0 ||
                outcome is not (null or 137 or 143))
            {
                var status = saved.State.Status is "completed" or "cancelled" or "failed" ? saved.State.Status :
                    store!.IsCancelled(e.State.RunId) ? "cancelled" : outcome == 0 ? "completed" : "failed";
                await FinishRecoveredAsync(e, status, outcome).ConfigureAwait(false);
                return;
            }
            if (task.Status is not ("exited" or "dead"))
                throw new RunSetupException("The task did not provide a definite inactive state. No resume was attempted.");
            e.TaskConfirmedStopped = true;
            Update(e, s => s with { MayBeRunning = false });
            if (e.DriverLease is null)
                throw new RunSetupException("Another dashboard owns recovery of this run. No duplicate resume or cleanup was attempted.");
            CheckCli(env.Help);
            if (ownership[2] != "SESSION" || e.State.SessionId is null)
                throw new RunSetupException("No readable saved Copilot session exists for the exact recorded ID. Work is retained; a fresh prompt is not a resume.");
            if (e.State.ResumeAttempts >= 1)
                throw new RunSetupException("The automatic resume attempt was already used. Work is preserved; inspect the session instead of restarting in a loop.");
            e.TaskConfirmedStopped = false;
            Update(e, s => s with { Status = "recovering", ResumeAttempts = s.ResumeAttempts + 1, Error = null, FinishedAt = null,
                MayBeRunning = true });
            e.TaskIdentity = null;
            Persist(e);
            var launched = await InvokeRun("launch", env, LaunchScript, LaunchArguments(e, resume: true),
                token: CancellationToken.None).ConfigureAwait(false);
            RequireSuccess(launched, "Exact-session resume could not be launched; the worktree remains preserved.");
            var resumed = ReadOwnedTask(e, await InvokeRun("status", env, StatusScript, [env.Bgtask, e.TaskName],
                token: token).ConfigureAwait(false));
            e.TaskIdentity = resumed.Id;
            Update(e, s => s with { Status = "running", LogPath = resumed.Log });
            e.Reconciled.TrySetResult();
            await FollowRecoveredAsync(e, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (e.Detaching) { Persist(e); }
        catch (OperationCanceledException) when (e.Cancellation.IsCancellationRequested)
        {
            try
            {
                var code = await StopRecoveredAsync(e).ConfigureAwait(false);
                await FinishRecoveredAsync(e, "cancelled", code).ConfigureAwait(false);
            }
            catch { RecoveryBlocked(e, "Could not verify cancellation of the exact owned task. Work and its reservation are retained."); }
        }
        catch (Exception ex)
        {
            RecoveryBlocked(e, ex is RunSetupException ? ex.Message :
                "Saved run recovery failed or its record is unreadable. Work is preserved; no successful resume or cleanup is claimed.");
        }
        finally
        {
            e.Reconciled.TrySetResult();
            if (!e.Detaching) { e.DriverLease?.Dispose(); e.DriverLease = null; }
            FlushNotifications();
        }
    }

    void RecoveryBlocked(Entry e, string message)
    {
        if (e.State.Status is "completed" or "cancelled" or "failed")
        {
            Update(e, s => s with { Error = message,
                WorktreeStatus = s.WorktreeStatus == "removed-clean" ? "removed-clean" : "retained-recovery" });
            return;
        }
        Update(e, s => s with { Status = "needs-attention", Error = message, WorktreeStatus = "retained-recovery" });
        lock (gate) activePrs.Add(PrIdentity(e.State.Request));
    }

    async Task FollowRecoveredAsync(Entry e, CancellationToken token)
    {
        var followed = await FollowAsync(e, token).ConfigureAwait(false);
        var code = await WaitForExitAsync(e, followed, token).ConfigureAwait(false);
        await FinishRecoveredAsync(e, store?.IsCancelled(e.State.RunId) == true ? "cancelled" :
            code == 0 ? "completed" : "failed", code).ConfigureAwait(false);
    }

    async Task FinishRecoveredAsync(Entry e, string status, int? code)
    {
        e.TaskConfirmedStopped = true;
        if (status == "failed" && e.State.ResumeAttempts > 0) status = "needs-attention";
        Update(e, s => s with { Status = status, ExitCode = code, MayBeRunning = false,
            FinishedAt = s.FinishedAt ?? DateTimeOffset.UtcNow,
            Error = status == "needs-attention" ?
                $"Saved-session continuation exited with code {code}. Inspect its output and WSL authentication/tools; work is retained and no further automatic resume is allowed." :
                status == "failed" ? $"Recorded Copilot task exited with code {code}; it is not automatically resumed." : null });
        lock (gate)
        {
            if (status == "needs-attention") activePrs.Add(PrIdentity(e.State.Request));
            else activePrs.Remove(PrIdentity(e.State.Request));
        }
        if (e.HistoryReplayStarted || e.State.Output.StartsWith("[Recovered run history", StringComparison.Ordinal))
        {
            var cancelling = status == "cancelled" || e.Cancellation.IsCancellationRequested;
            var unavailable = e.HistoryReplayStarted && !e.HistoryReplayCompleted;
            if (!e.HistoryReplayStarted)
            {
                using var history = CancellationTokenSource.CreateLinkedTokenSource(e.DetachCancellation.Token,
                    cancelling ? CancellationToken.None : e.Cancellation.Token);
                if (cancelling) history.CancelAfter(TimeSpan.FromSeconds(2));
                try { await ReadHistoryAsync(e, history.Token).ConfigureAwait(false); }
                catch (Exception ex) when (cancelling && !e.Detaching &&
                    ex is OperationCanceledException or RunSetupException or IOException or System.ComponentModel.Win32Exception)
                { unavailable = true; }
            }
            if (unavailable)
            {
                Update(e, s => s with { Error = (s.Error is null ? "" : s.Error + "\n") +
                    "Saved task logs could not be fully replayed after Stop. Verified stopped state, retained logs and work are preserved." });
            }
        }
        if (status == "completed" && e.DriverLease is not null)
        {
            try
            {
                using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(e.Cancellation.Token, e.DetachCancellation.Token);
                cleanup.Token.ThrowIfCancellationRequested();
                var cleaned = await InvokeRun("cleanup", e.Environment!, CleanupScript, CleanupArguments(e),
                    token: cleanup.Token).ConfigureAwait(false);
                var fields = ProtocolFields(cleaned.StandardOutput);
                Update(e, s => s with { WorktreeStatus = cleaned.ExitCode == 0 && fields.FirstOrDefault() == "REMOVED" ?
                    "removed-clean" : "retained-changes", Error = fields.FirstOrDefault() == "REMOVED" ? null :
                    "Completed; worktree retained: " + (fields.ElementAtOrDefault(1) ?? "safe ownership/liveness/publication could not be verified.") });
            }
            catch { Update(e, s => s with { WorktreeStatus = "retained", Error = "Completed; cleanup failed. Worktree retained; completion is not resumable." }); }
        }
    }

    async Task ReadHistoryAsync(Entry e, CancellationToken token)
    {
        if (e.Environment is null) return;
        var env = e.Environment;
        ReadOwnedTask(e, await InvokeRun("status", env, StatusScript, [env.Bgtask, e.TaskName], token: token).ConfigureAwait(false));
        using var replay = CancellationTokenSource.CreateLinkedTokenSource(token);
        var replayToken = replay.Token;
        e.HistoryReplayStarted = true;
        try
        {
            var logs = await InvokeRun("history", env, "\"$1\" logs \"$2\" --tail 2000", [env.Bgtask, e.TaskName],
                stdout: s => Append(e, s, false, history: true, token: replayToken),
                stderr: s => Append(e, s, true, history: true, token: replayToken), token: replayToken).ConfigureAwait(false);
            RequireSuccess(logs, "Saved task logs could not be read; terminal history and retained work remain recorded.");
            e.HistoryReplayCompleted = true;
        }
        finally { replay.Cancel(); }
    }

    Task<CopilotProcessResult> ProbeRunAsync(Entry e, CancellationToken token = default) =>
        InvokeRun("verify-run", e.Environment!, RecoveryProbeScript,
            [e.Environment!.Root, e.Environment.CommonDirectory, e.RunDirectory!, e.State.RunId,
                e.State.Request.SourceCommit.ToLowerInvariant(), e.OwnershipToken, e.State.SessionId ?? "", e.Environment.CopilotHome], token: token);

    async Task<string[]> VerifyRunAsync(Entry e, CancellationToken token = default)
    {
        var result = await ProbeRunAsync(e, token).ConfigureAwait(false);
        RequireSuccess(result, "Saved workspace ownership/branch/namespace could not be verified. No task was resumed or worktree removed.");
        var fields = ProtocolFields(result.StandardOutput);
        if (fields.Length < 4 || fields[0] is not ("OWNED" or "REMOVED"))
            throw new RunSetupException("Workspace ownership returned an invalid receipt; work is preserved.");
        return fields;
    }

    sealed record OwnedTask(string Id, string Status, int? ExitCode, string? Log);
    static OwnedTask ReadOwnedTask(Entry e, CopilotProcessResult result)
    {
        RequireSuccess(result, "Live managed-task status is unavailable; liveness is unknown and work is preserved.");
        try
        {
            using var doc = JsonDocument.Parse(result.StandardOutput);
            var root = doc.RootElement;
            var id = root.GetProperty("id").GetString()!;
            var args = root.GetProperty("command").EnumerateArray().Select(v => v.GetString()).ToArray();
            if (id.Length == 0 || e.TaskIdentity is not null && e.TaskIdentity != id ||
                root.GetProperty("name").GetString() != e.TaskName ||
                root.GetProperty("cwd").GetString() != e.State.WorktreePath ||
                !args.TakeLast(6).SequenceEqual(new[] { "ado-prview-runner", e.Environment!.Copilot, e.RunDirectory,
                    e.State.SessionId, e.OwnershipToken, args.LastOrDefault() }) ||
                args.LastOrDefault() != Mode(e, false) && args.LastOrDefault() != Mode(e, true))
                throw new RunSetupException("Managed task identity differs from the owned launch receipt. No task was restarted or stopped.");
            var status = root.GetProperty("status");
            var state = status.GetProperty("state").GetString()!;
            return new(id, state, state == "exited" ? status.GetProperty("exited").GetProperty("code").GetInt32() : null,
                root.TryGetProperty("log", out var log) ? log.GetString() : null);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new RunSetupException("Managed task returned incomplete/ambiguous identity or status. Work is preserved."); }
    }

    async Task<int?> StopRecoveredAsync(Entry e)
    {
        await VerifyRunAsync(e).ConfigureAwait(false);
        var env = e.Environment!;
        var before = ReadOwnedTask(e, await InvokeRun("status", env, StatusScript, [env.Bgtask, e.TaskName]).ConfigureAwait(false));
        if (before.Status != "exited")
            RequireSuccess(await InvokeRun("stop", env, StopScript, [env.Bgtask, e.TaskName]).ConfigureAwait(false),
                "The exact recovered task could not be stopped.");
        var after = ReadOwnedTask(e, await InvokeRun("status", env, StatusScript, [env.Bgtask, e.TaskName]).ConfigureAwait(false));
        var ownership = await VerifyRunAsync(e).ConfigureAwait(false);
        if (after.Status != "exited" || ownership[1] != "FREE")
            throw new RunSetupException("The recovered task or its CLI process group is still active; cancellation could not be confirmed.");
        return after.ExitCode;
    }

    internal sealed class DurableStore(string directory)
    {
        public string DirectoryPath => Path.GetFullPath(directory);
        void Ensure()
        {
            Directory.CreateDirectory(directory);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Copilot run history cannot use a linked directory.");
        }
        string PathFor(string id, string extension)
        {
            if (!Regex.IsMatch(id, @"\A[0-9a-f]{32}\z")) throw new InvalidOperationException("Invalid saved run ID.");
            return Path.Combine(directory, id + extension);
        }
        FileStream RegistryLock()
        {
            Ensure();
            try { return new(Path.Combine(directory, "registry.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { throw new InvalidOperationException("Another dashboard is updating run history. Retry after it completes; no duplicate run was started."); }
        }
        public FileStream? TryLease(string id)
        {
            Ensure();
            try { return new(PathFor(id, ".lease"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { return null; }
        }
        public FileStream Reserve(SavedRun saved, Func<CopilotRunRequest, string> identity)
        {
            using var guard = RegistryLock();
            foreach (var (old, error) in Load())
            {
                if (error is not null) throw new InvalidOperationException("Saved Copilot history needs attention; an unreadable record could contain an active PR reservation.");
                if (old is not null && ReservesPr(old) && identity(old.State.Request).Equals(identity(saved.State.Request), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("A Copilot run for this PR is already active or needs recovery in another dashboard.");
            }
            var lease = TryLease(saved.State.RunId) ?? throw new InvalidOperationException("The new run reservation is already held.");
            try { Write(saved); return lease; } catch { lease.Dispose(); throw; }
        }
        public IEnumerable<(SavedRun? Saved, string? Diagnostic)> Load()
        {
            Ensure();
            foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
            {
                SavedRun? saved = null;
                try
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException();
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    saved = JsonSerializer.Deserialize<SavedRun>(stream);
                    if (saved is null || saved.Version != 1 || Path.GetFileNameWithoutExtension(path) != saved.State.RunId ||
                        !Regex.IsMatch(saved.OwnershipToken, @"\A[0-9a-f]{64}\z") || !Guid.TryParseExact(saved.State.SessionId, "D", out _) ||
                        saved.State.Request.Prompt.Length != 0 || saved.State.Output.Length != 0 ||
                        saved.State.Status is not ("preparing" or "running" or "recovering" or "cancelling" or "needs-attention" or "completed" or "failed" or "cancelled") ||
                        saved.State.Request.Id <= 0 || string.IsNullOrWhiteSpace(saved.State.Request.Org) ||
                        string.IsNullOrWhiteSpace(saved.State.Request.Project) || string.IsNullOrWhiteSpace(saved.State.Request.Repo) ||
                        saved.State.Backend is not ("legacy-wsl" or "windows-native") ||
                        saved.Native is not null && (saved.State.Backend != "windows-native" ||
                            saved.Native.RunId != saved.State.RunId || saved.Native.Token != saved.OwnershipToken ||
                            saved.Native.SessionId != saved.State.SessionId || saved.Native.RunDirectory != saved.RunDirectory ||
                            saved.Native.PushToSource != saved.State.Request.PushToSource ||
                            saved.Native.Worktree != saved.State.WorktreePath && (saved.State.WorktreePath is not null || saved.LaunchIntent) ||
                            saved.Native.SourceDirectory != saved.State.SourcePath ||
                            !saved.Native.Commit.Equals(saved.State.Request.SourceCommit, StringComparison.OrdinalIgnoreCase)))
                        throw new IOException();
                    PathFor(saved.State.RunId, ".json");
                }
                catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or NullReferenceException or UnauthorizedAccessException or InvalidOperationException)
                { saved = null; }
                yield return (saved, saved is null ? "A saved Copilot record is corrupt, incomplete or unsupported. Files/worktrees were preserved; repair the private CopilotRuns history before launching more work." : null);
            }
        }
        public void Write(SavedRun saved)
        {
            Ensure();
            var target = PathFor(saved.State.RunId, ".json");
            var temporary = PathFor(saved.State.RunId, ".tmp");
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, saved);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, target, overwrite: true);
        }
        public void RequestCancel(string id)
        {
            Ensure();
            using var file = new FileStream(PathFor(id, ".cancelled"), FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
            file.Flush(flushToDisk: true);
        }
        public bool IsCancelled(string id) => File.Exists(PathFor(id, ".cancelled"));
    }

    internal const string RecoveryProbeScript = """
        root="$1"; common="$2"; run="$3"; id="$4"; commit="$5"; nonce="$6"; session="$7"; copilot_home="$8"
        [[ "$run" == "$HOME/.local/state/ado-prview/runs/$id" && "$id" =~ ^[0-9a-f]{32}$ ]] || exit 77
        [[ -d "$run" && -O "$run" && ! -L "$run" && -f "$run/ownership" && -O "$run/ownership" && ! -L "$run/ownership" ]] || exit 77
        mapfile -t receipt < "$run/ownership"
        [[ "${receipt[0]-}" == "$root" && "${receipt[1]-}" == ado-prview/pr-*-"$id" && "${receipt[2]-}" == "$commit" &&
           "${receipt[3]-}" == "$nonce" && "${receipt[4]-}" == "$session" ]] || exit 77
        [[ "$(cd -- "$root" && cd -- "$(git rev-parse --git-common-dir)" && pwd -P)" == "$common" ]] || exit 77
        lock=LOCKED
        [[ ! -L "$run/task.lock" ]] || exit 77
        exec 8>"$run/task.lock"; if flock -n 8; then lock=FREE; fi
        saved=MISSING
        events="$copilot_home/session-state/$session/events.jsonl"
        if [[ -f "$events" && -s "$events" && -O "$events" && ! -L "$events" ]]; then saved=SESSION; fi
        outcome=NONE
        if [[ -f "$run/outcome" && -O "$run/outcome" && ! -L "$run/outcome" ]]; then
          mapfile -t result < "$run/outcome"
          [[ "${result[0]-}" == "$nonce" && "${result[1]-}" == "$session" && "${result[2]-}" =~ ^[0-9]+$ ]] || exit 77
          outcome="${result[2]}"
        fi
        if [[ ! -e "$run/worktree" ]]; then
          printf 'ADOPRVIEW1\0REMOVED\0%s\0%s\0%s\0' "$lock" "$saved" "$outcome"; exit 0
        fi
        [[ ! -L "$run/worktree" && "$(git -C "$run/worktree" rev-parse --show-toplevel)" == "$run/worktree" &&
           "$(git -C "$run/worktree" rev-parse --path-format=absolute --git-common-dir)" == "$common" &&
           "$(git -C "$run/worktree" symbolic-ref HEAD)" == "refs/heads/${receipt[1]}" ]] || exit 77
        printf 'ADOPRVIEW1\0OWNED\0%s\0%s\0%s\0' "$lock" "$saved" "$outcome"
        """;
}
