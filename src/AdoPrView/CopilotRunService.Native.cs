using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AdoPrView;

public sealed partial class CopilotRunService
{
    internal static string NativeExecutable(string name, string? configured = null, string? searchPath = null)
    {
        if (configured is not null)
        {
            if (!Path.IsPathFullyQualified(configured) || !configured.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(configured))
                throw new RunSetupException($"The configured native {name} executable is missing or is not an absolute .exe path.");
            return Path.GetFullPath(configured);
        }
        foreach (var directory in (searchPath ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var candidate = Path.Combine(directory.Trim('"'), name + ".exe");
            if (Path.IsPathFullyQualified(candidate) && File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        var prerequisite = name == "git"
            ? "Install Git for Windows and make git.exe available on PATH."
            : "Install the full native Windows Copilot CLI and make copilot.exe available on PATH (or supply an absolute NativeCopilotPath). The bundled SDK runtime and npm shell shims are not prompt executables.";
        throw new RunSetupException($"Native Windows {name}.exe was not found on PATH. {prerequisite} New Fix runs do not invoke WSL.");
    }

    internal static async Task<CopilotProcessResult> NativeCommandAsync(string executable, string directory,
        IReadOnlyList<string> arguments, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment.Clear();
        foreach (var pair in ExplainModel.ChildEnvironment()) start.Environment[pair.Key] = pair.Value;
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = "never";
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new RunSetupException("A native Windows prerequisite process could not be started.");
        process.StandardInput.Close();
        // Preparatory tools are bounded and attached. The persistent CLI is owned by the separate job supervisor.
        using var registration = token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
        });
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token).ConfigureAwait(false);
        return new(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    async Task<string> NativeGitAsync(string git, string root, CancellationToken token, params string[] arguments)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var result = await NativeCommandAsync(git, root,
            ["-c", "core.hooksPath=NUL", "-c", "credential.interactive=false", .. arguments], timeout.Token).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            if (new[] { "Authentication failed", "could not read Username", "terminal prompts disabled", "interaction is disabled" }
                .Any(marker => result.StandardError.Contains(marker, StringComparison.OrdinalIgnoreCase)))
                throw new RunSetupException("Native Windows Git authentication failed or requires interaction. Sign in with the chosen Windows Git credential helper outside this non-interactive run. No tokens/configuration were copied; work is preserved.");
            throw new RunSetupException("Native Git preparation/ownership verification failed. Check the configured Windows checkout and exact captured source revision. No sign-in dialog is available; work is preserved.");
        }
        return result.StandardOutput.TrimEnd('\r', '\n');
    }

    async Task<NativeFixSupervisor.Specification> PrepareNativeAsync(Entry e, CancellationToken token)
    {
        var request = e.State.Request;
        var root = Path.GetFullPath(request.Directory!);
        NativeFixSupervisor.SafePath(root);
        if (!System.IO.Directory.Exists(root))
            throw new RunSetupException("The configured native Windows repository directory does not exist. No Copilot task was launched.");
        var git = NativeExecutable("git");
        var cli = NativeExecutable("copilot", request.NativeCopilotPath);
        using (var helpTimeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            helpTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            var help = await NativeCommandAsync(cli, root, ["--no-auto-update", "--help"], helpTimeout.Token).ConfigureAwait(false);
            var required = new[] { "--no-ask-user", "--stream", "--no-color", "--deny-tool", "--yolo", "--session-id", "--resume", "--no-auto-update" };
            var missing = required.Where(option => !Regex.IsMatch(help.StandardOutput,
                @"(?<![\w-])" + Regex.Escape(option) + @"(?![\w-])")).ToArray();
            if (help.ExitCode != 0 || missing.Length != 0)
                throw new RunSetupException("The selected native Windows Copilot executable is not a compatible full CLI. " +
                    "The bundled SDK stdio runtime is not a prompt CLI. Check native 'copilot --help'; required flags missing: " +
                    string.Join(", ", missing) + ". No task was launched; no CLI/account configuration was changed.");
        }
        var actualRoot = Path.GetFullPath(await NativeGitAsync(git, root, token, "rev-parse", "--show-toplevel").ConfigureAwait(false));
        if (!actualRoot.Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
            throw new RunSetupException("The configured Windows directory must be the repository root, not a nested directory.");
        var common = Path.GetFullPath(await NativeGitAsync(git, root, token, "rev-parse", "--path-format=absolute", "--git-common-dir").ConfigureAwait(false));
        NativeFixSupervisor.SafePath(common);
        string? remote = null, fetch = null;
        string[] push = [];
        foreach (var candidate in (await NativeGitAsync(git, root, token, "remote").ConfigureAwait(false)).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = candidate.TrimEnd('\r');
            if (name.StartsWith('-')) continue;
            var url = await NativeGitAsync(git, root, token, "remote", "get-url", name).ConfigureAwait(false);
            if (!RemoteMatches(url, request.Org, request.Project, request.Repo) || HasCredential(url)) continue;
            var destinations = request.PushToSource
                ? (await NativeGitAsync(git, root, token, "remote", "get-url", "--push", "--all", name)
                    .ConfigureAwait(false)).Split('\n').Select(v => v.TrimEnd('\r')).ToArray()
                : Array.Empty<string>();
            if (request.PushToSource && (destinations.Length == 0 ||
                destinations.Any(v => HasCredential(v) || !RemoteMatches(v, request.Org, request.Project, request.Repo)))) continue;
            remote = name; fetch = url; push = destinations; break;
        }
        if (remote is null || fetch is null)
            throw new RunSetupException("The configured Windows checkout has no credential-free Git remote whose fetch URL " +
                (request.PushToSource ? "and every effective push URL " : "") +
                "match this PR's exact organization, project and repository. No worktree/task was created; Git configuration was not changed.");
        await NativeGitAsync(git, root, token, "check-ref-format", request.SourceRef).ConfigureAwait(false);
        var source = await NativeGitAsync(git, root, token, "ls-remote", "--refs", remote, request.SourceRef).ConfigureAwait(false);
        if (source.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) is not [var hash, var reference] ||
            !hash.Equals(request.SourceCommit, StringComparison.OrdinalIgnoreCase) || reference != request.SourceRef)
            throw new RunSetupException("The PR source branch no longer matches the captured revision. Refresh the PR before starting a new native Fix run.");
        await NativeGitAsync(git, root, token, "fetch", "--no-tags", "--no-recurse-submodules", "--no-write-fetch-head", "--refmap=",
            remote, request.SourceRef).ConfigureAwait(false);
        var commit = await NativeGitAsync(git, root, token, "rev-parse", "--verify", request.SourceCommit + "^{commit}").ConfigureAwait(false);
        if (!commit.Equals(request.SourceCommit, StringComparison.OrdinalIgnoreCase))
            throw new RunSetupException("Native Git did not verify the exact source commit; no task was launched.");
        var runDirectory = Path.Combine(store!.DirectoryPath, "native", e.State.RunId);
        NativeFixSupervisor.SafePath(runDirectory);
        if (System.IO.Directory.Exists(runDirectory)) throw new RunSetupException("The new native run directory already exists; nothing was reused.");
        System.IO.Directory.CreateDirectory(runDirectory);
        var worktree = Path.Combine(runDirectory, "worktree");
        var branch = $"ado-prview/pr-{request.Id}-{e.State.RunId}";
        var specification = new NativeFixSupervisor.Specification(e.State.RunId, e.OwnershipToken, e.State.SessionId!,
            runDirectory, root, common, worktree, branch, commit, git, cli, remote, fetch, push, request.PushToSource);
        NativeFixSupervisor.Write(Path.Combine(runDirectory, "ownership.json"), specification);
        e.Native = specification; e.RunDirectory = runDirectory;
        Update(e, s => s with { SourcePath = root, WorktreeStatus = "preparing",
            PreparationLogPath = Path.Combine(runDirectory, "stderr.log") });
        // Complete Git metadata updates without interruption, then honor cancellation before any CLI launch.
        await NativeGitAsync(git, root, CancellationToken.None, "worktree", "add", "-b", branch, worktree, commit).ConfigureAwait(false);
        Update(e, s => s with { WorktreePath = worktree, WorktreeStatus = "created" });
        token.ThrowIfCancellationRequested();
        await VerifyNativeWorkspaceAsync(e, token).ConfigureAwait(false);
        return specification;
    }

    static bool HasCredential(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == "https" && uri.UserInfo.Length != 0 || uri.Scheme == "ssh" && uri.UserInfo.Contains(':'));

    static string PsQuote(string value) => "'" + value.Replace("'", "''") + "'";

    internal static string NativePublishInstructions(CopilotRunRequest request, NativeFixSupervisor.Specification s)
    {
        if (!request.PushToSource) return "\n## Publication\nThis run is not authorized to push; git push is denied. Preserve local work and report it as unpublished.\n";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", s.PushUrls)))).ToLowerInvariant();
        return "\n## Verified Windows publish target\n" +
            $"Remote {PsQuote(s.Remote)} fetch and every effective push URL were verified against this PR's exact repository. " +
            $"Captured source ref is {request.SourceRef}, commit {request.SourceCommit}. " +
            "Only after validating the requested fix and all existing approval/policy gates, use native PowerShell to recheck push destinations: " +
            $"$urls = @(git remote get-url --push --all {PsQuote(s.Remote)}); if ($LASTEXITCODE -ne 0) {{ throw 'Push URL check failed' }}; " +
            "$sha = [Security.Cryptography.SHA256]::Create(); try { " +
            "$actual = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($urls -join \"`n\"))))).Replace('-', '').ToLowerInvariant() " +
            "} finally { $sha.Dispose() }; " +
            $"if ($actual -ne {PsQuote(hash)}) {{ throw 'Push destinations changed; do not push or edit Git configuration' }}; " +
            $"git push --no-follow-tags --recurse-submodules=check {PsQuote(s.Remote)} {PsQuote("HEAD:" + request.SourceRef)}; " +
            $"if ($LASTEXITCODE -ne 0) {{ throw 'Publication failed; keep local work' }}; git ls-remote {PsQuote(s.Remote)} {PsQuote(request.SourceRef)}. " +
            "Read back the fresh PR and intended source commit. Ordinary source-branch push only: no force, other branches/tags, submodule pushes, merge, approval or policy bypass. " +
            "This is a prompt scope restriction, not a filesystem or publication sandbox. If a gate/check fails, preserve edits and report blocked publication.\n";
    }

    async Task VerifyNativeWorkspaceAsync(Entry e, CancellationToken token)
    {
        var s = e.Native ?? throw new RunSetupException("The saved native ownership specification is missing; no task was replayed.");
        NativeFixSupervisor.Verify(s);
        NativeFixSupervisor.SafePath(s.Worktree);
        var root = Path.GetFullPath(await NativeGitAsync(s.Git, s.Worktree, token, "rev-parse", "--show-toplevel").ConfigureAwait(false));
        var common = Path.GetFullPath(await NativeGitAsync(s.Git, s.Worktree, token, "rev-parse", "--path-format=absolute", "--git-common-dir").ConfigureAwait(false));
        var branch = await NativeGitAsync(s.Git, s.Worktree, token, "symbolic-ref", "HEAD").ConfigureAwait(false);
        if (!root.Equals(s.Worktree, StringComparison.OrdinalIgnoreCase) ||
            !common.Equals(s.CommonDirectory, StringComparison.OrdinalIgnoreCase) || branch != "refs/heads/" + s.Branch ||
            s.RunId != e.State.RunId || s.Token != e.OwnershipToken || s.SessionId != e.State.SessionId ||
            s.PushToSource != e.State.Request.PushToSource)
            throw new RunSetupException("The saved native worktree/session ownership changed. No session was restarted, cleanup attempted or unrelated process stopped.");
    }

    async Task ExecuteNativeAsync(Entry e)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(e.Cancellation.Token, e.DetachCancellation.Token);
        try
        {
            var spec = await PrepareNativeAsync(e, lifetime.Token).ConfigureAwait(false);
            var prompt = AppendAdditionalInstructions(e.State.Request.Prompt +
                NativePublishInstructions(e.State.Request, spec), e.State.Request.AdditionalInstructions);
            ValidatePrompt(prompt);
            await File.WriteAllTextAsync(NativeFixSupervisor.FilePath(spec, "prompt"), prompt, new UTF8Encoding(false), lifetime.Token).ConfigureAwait(false);
            lifetime.Token.ThrowIfCancellationRequested();
            e.LaunchIntent = true;
            Update(e, s => s with { MayBeRunning = true, LogPath = NativeFixSupervisor.FilePath(spec, "stdout.log") });
            try { NativeFixSupervisor.Launch(spec); }
            catch { e.TaskConfirmedStopped = true; throw; }
            Update(e, s => s with { Status = "running", WorktreeStatus = "in-use" });
            await FollowNativeAsync(e, lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (e.Detaching) { Persist(e); }
        catch (OperationCanceledException) when (e.Cancellation.IsCancellationRequested)
        {
            if (e.LaunchIntent && !e.TaskConfirmedStopped)
                await RetryNativeStopAsync(e).ConfigureAwait(false);
            else NativeFinish(e, "cancelled", null, "Cancelled before native Copilot launch. Any prepared worktree is retained.");
        }
        catch (Exception ex)
        {
            NativeFinish(e, e.LaunchIntent && !e.TaskConfirmedStopped ? "needs-attention" : "failed", null,
                ex is RunSetupException ? ex.Message : ex is Win32Exception w
                    ? $"Native Windows launch failed (Windows error {w.NativeErrorCode}). A host job may prohibit detached breakaway; no WSL fallback was attempted."
                    : "Native Windows Fix preparation/supervision failed. Inspect the native prerequisites and retained run; no successful fix is claimed.");
        }
        finally
        {
            if (!e.Detaching)
            {
                DiscardNativePrompt(e);
                e.DriverLease?.Dispose(); e.DriverLease = null;
            }
            e.Reconciled.TrySetResult();
            FlushNotifications();
        }
    }

    async Task RecoverNativeAsync(Entry e, SavedRun saved)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(e.Cancellation.Token, e.DetachCancellation.Token);
        try
        {
            if (saved.Native is null)
            {
                if (!saved.LaunchIntent && saved.State.Status is "failed" or "cancelled") return;
                throw new RunSetupException("Native preparation was interrupted before an ownership receipt. Work is preserved; no prompt or session was replayed.");
            }
            // Stop/history do not require today's CLI or remote/authentication configuration.
            NativeFixSupervisor.Verify(saved.Native);
            if (!saved.LaunchIntent)
            {
                if (saved.State.IsActive)
                    throw new RunSetupException("Native preparation was interrupted before launch. Inspect the retained worktree; the original prompt is not replayed.");
                ReplayNativeLogs(e);
                return;
            }
            e.Reconciled.TrySetResult();
            await FollowNativeAsync(e, lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (e.Detaching) { Persist(e); }
        catch (OperationCanceledException) when (e.Cancellation.IsCancellationRequested)
        { await RetryNativeStopAsync(e).ConfigureAwait(false); }
        catch (Exception ex)
        {
            RecoveryBlocked(e, ex is RunSetupException ? ex.Message :
                "Native run ownership/liveness could not be verified. Its work, saved session and same-PR reservation are preserved; no fresh prompt was replayed.");
        }
        finally
        {
            e.Reconciled.TrySetResult();
            if (!e.Detaching) { e.DriverLease?.Dispose(); e.DriverLease = null; }
            FlushNotifications();
        }
    }

    async Task FollowNativeAsync(Entry e, CancellationToken token)
    {
        var s = e.Native!;
        using var stdout = new NativeLogTail(NativeFixSupervisor.FilePath(s, "stdout.log"));
        using var stderr = new NativeLogTail(NativeFixSupervisor.FilePath(s, "stderr.log"));
        var startup = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Append(e, stdout.Read(), false, history: true, token: token);
            Append(e, stderr.Read(), true, history: true, token: token);
            var receipt = NativeFixSupervisor.Status(s);
            var active = NativeFixSupervisor.ActiveProcesses(s);
            if (receipt is { DescendantsStopped: true, ExitCode: not null } && active is null or 0)
            {
                e.TaskConfirmedStopped = true;
                DrainNativeLog(e, stdout, false, token);
                DrainNativeLog(e, stderr, true, token);
                NativeFinish(e, store!.IsCancelled(e.State.RunId) || e.Cancellation.IsCancellationRequested ? "cancelled" :
                    receipt.ExitCode == 0 ? "completed" : "failed", receipt.ExitCode, receipt.Error ??
                    (receipt.ExitCode == 0 ? null : NativeExitError(e)));
                return;
            }
            if (receipt is not null && !NativeFixSupervisor.SupervisorAlive(receipt) && active is null or 0)
            {
                e.TaskConfirmedStopped = true;
                NativeFinish(e, store!.IsCancelled(e.State.RunId) || e.Cancellation.IsCancellationRequested ? "cancelled" : "needs-attention", null,
                    "The native supervisor exited without a completion receipt. Its kill-on-close job is inactive; work and exact saved session are retained. No automatic replay/restart was attempted.");
                return;
            }
            if (receipt is null && startup.Elapsed > TimeSpan.FromSeconds(15))
                throw new RunSetupException("Native supervisor startup was not confirmed. Work and its reservation are retained; no duplicate prompt was launched.");
            if (e.State.Status != "running" || !e.State.MayBeRunning)
                Update(e, state => state with { Status = "running", MayBeRunning = true, FinishedAt = null, Error = null });
            if (store!.IsCancelled(e.State.RunId) && e.DriverLease is not null) NativeFixSupervisor.RequestCancel(s);
            e.Reconciled.TrySetResult();
            await Task.Delay(150, token).ConfigureAwait(false);
        }
    }

    async Task RetryNativeStopAsync(Entry e)
    {
        var acquired = e.DriverLease is null;
        try
        {
            e.DriverLease ??= store!.TryLease(e.State.RunId);
            if (e.DriverLease is null) throw new RunSetupException("Another dashboard owns this run; cancellation is recorded for its exact supervisor.");
            if (!e.LaunchIntent)
            {
                e.TaskConfirmedStopped = true;
                NativeFinish(e, "cancelled", null, "Preparation cancelled; work is retained.");
                return;
            }
            if (e.Native is null) throw new RunSetupException("The native launch ownership receipt is missing. No unrelated process was stopped.");
            NativeFixSupervisor.RequestCancel(e.Native);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await FollowNativeAsync(e, timeout.Token).ConfigureAwait(false);
            if (!e.TaskConfirmedStopped) throw new RunSetupException("Native descendant stop was not confirmed.");
            e.UncertainStop = false;
        }
        catch
        {
            e.UncertainStop = true;
            NativeFinish(e, "needs-attention", null,
                "Cancellation of the exact native job could not be confirmed. Work and the PR reservation are retained; no unrelated PID/process name was killed.");
        }
        finally
        {
            if (acquired) { e.DriverLease?.Dispose(); e.DriverLease = null; }
        }
    }

    void NativeFinish(Entry e, string status, int? code, string? error)
    {
        var workspace = e.State.WorktreePath ?? (e.Native is not null && System.IO.Directory.Exists(e.Native.Worktree)
            ? e.Native.Worktree : null);
        Update(e, s => s with { Status = status, ExitCode = code, Error = error,
            SourcePath = e.Native?.SourceDirectory ?? s.SourcePath, WorktreePath = workspace,
            MayBeRunning = e.LaunchIntent && !e.TaskConfirmedStopped,
            FinishedAt = e.LaunchIntent && !e.TaskConfirmedStopped ? null : DateTimeOffset.UtcNow,
            WorktreeStatus = workspace is null ? "not-created" : "retained" });
        lock (gate)
        {
            if (e.State.MayBeRunning || status == "needs-attention") activePrs.Add(PrIdentity(e.State.Request));
            else activePrs.Remove(PrIdentity(e.State.Request));
        }
    }

    string NativeExitError(Entry e)
    {
        string output;
        lock (gate) { CaptureOutput(e); output = e.State.Output; }
        if (new[] { "not logged in", "not authenticated", "not signed in", "authentication required", "copilot login" }
            .Any(marker => output.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            return "The chosen native Windows Copilot CLI output reports a sign-in/authentication problem. Run that CLI's own 'copilot login' outside the dashboard, then retry. WSL/Explain authentication and copied tokens are not used.";
        return "Native Copilot exited unsuccessfully. Inspect its output for Windows CLI tool/dependency errors; no verified fix is claimed.";
    }

    void ReplayNativeLogs(Entry e)
    {
        using var stdout = new NativeLogTail(NativeFixSupervisor.FilePath(e.Native!, "stdout.log"));
        using var stderr = new NativeLogTail(NativeFixSupervisor.FilePath(e.Native!, "stderr.log"));
        DrainNativeLog(e, stdout, false, CancellationToken.None);
        DrainNativeLog(e, stderr, true, CancellationToken.None);
    }

    void DrainNativeLog(Entry e, NativeLogTail log, bool stderr, CancellationToken token)
    {
        string text;
        while ((text = log.Read()).Length != 0)
        {
            token.ThrowIfCancellationRequested();
            Append(e, text, stderr, history: true, token: token);
        }
    }

    static void DiscardNativePrompt(Entry e)
    {
        if (e.Native is null || e.LaunchIntent && !e.TaskConfirmedStopped) return;
        try { NativeFixSupervisor.Verify(e.Native); File.Delete(NativeFixSupervisor.FilePath(e.Native, "prompt")); }
        catch { /* Retain ownership/work when private-file cleanup cannot be verified. */ }
    }

    sealed class NativeLogTail(string path) : IDisposable
    {
        FileStream? reader;
        readonly Decoder decoder = new UTF8Encoding(false).GetDecoder();
        public string Read()
        {
            if (reader is null)
            {
                if (!File.Exists(path)) return "";
                NativeFixSupervisor.SafePath(path);
                reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            var text = new StringBuilder();
            var bytes = new byte[4096];
            var chars = new char[4098];
            int count;
            while (text.Length < MaxOutputCharacters && (count = reader.Read(bytes, 0, bytes.Length)) != 0)
            {
                var characters = decoder.GetChars(bytes, 0, count, chars, 0, flush: false);
                text.Append(chars, 0, characters);
            }
            return text.ToString();
        }
        public void Dispose() => reader?.Dispose();
    }
}
