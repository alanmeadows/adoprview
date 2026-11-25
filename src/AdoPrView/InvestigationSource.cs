using System.IO;
using System.Text.Json;

namespace AdoPrView;

internal sealed record SourceWindow(string Path, string Side, int Start, string[] Lines);

internal interface IInvestigationSource : IAsyncDisposable
{
    ExplainContext Context { get; }
    string Location { get; }
    string[] Evidence { get; }
    ReferencePrContext[] References => [];
    Task<string> ReadFileAsync(string path, string side, int startLine, CancellationToken ct);
    Task<string> ListDirectoryAsync(string path, string side, CancellationToken ct);
    void ValidateFinding(LensFinding finding);
    Task VerifyAsync(CancellationToken ct);
}

internal sealed class InvestigationSource : IInvestigationSource
{
    readonly ICopilotRunTransport _transport;
    readonly string _distro, _root, _worktree, _id, _remote, _remoteUrl;
    readonly List<SourceWindow> _windows = [];
    readonly List<string> _evidence = [];
    readonly object _evidenceGate = new();
    readonly Action? _checkAvailable;
    int _reads;
    Exception? _failure;
    bool _disposed;
    public ExplainContext Context { get; }
    public string Location => $"{_distro}: {_worktree} (isolated Git worktree; pinned object reads, no checkout filters or repository code executed)";
    public string[] Evidence { get { lock (_evidenceGate) return Context.Evidence().Concat(_evidence).ToArray(); } }

    InvestigationSource(ExplainContext context, ICopilotRunTransport transport, string distro, string root, string worktree, string id,
        string remote, string remoteUrl, Action? checkAvailable)
    { Context = context; _transport = transport; _distro = distro; _root = root; _worktree = worktree; _id = id; _remote = remote; _remoteUrl = remoteUrl; _checkAvailable = checkAvailable; }

    static async Task<string> Execute(ICopilotRunTransport transport, string? distro, string script,
        string[] args, CancellationToken ct)
    {
        var result = await transport.ExecuteAsync(new("investigation-source", distro,
            CopilotRunService.SourceEnvironmentScript + "\nexport GIT_NO_REPLACE_OBJECTS=1\n" + script, args), null, null, ct);
        ct.ThrowIfCancellationRequested();
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Worktree source operation failed (exit {result.ExitCode}). Check the configured WSL checkout, Git authentication and PR revisions. No model shell or ADO write was run.");
        return result.StandardOutput;
    }

    public static async Task<IInvestigationSource> CreateAsync(ExplainContext context, PrView pr, AppSettings settings,
        CancellationToken ct, ICopilotRunTransport? transport = null, Action? checkAvailable = null)
    {
        transport ??= new WslCopilotRunTransport();
        settings.WslRepoDirectories.TryGetValue($"{pr.Org}/{pr.Project}/{pr.Repo}".ToLowerInvariant(), out var directory);
        var output = await Execute(transport, settings.WslDistro, DiscoverScript,
            [directory ?? "", pr.Repo], ct);
        var fields = output.Split('\0');
        if (fields.Length < 7 || fields[0] != "ADOINSPECT1" || fields[1].Length == 0 || !fields[2].StartsWith('/') || !fields[3].StartsWith('/'))
            throw new InvalidDataException("WSL did not confirm the source checkout identity.");
        string? remote = null, remoteUrl = null;
        for (var i = 4; i + 1 < fields.Length; i += 2)
            if (CopilotRunService.RemoteMatches(fields[i + 1], pr.Org, pr.Project, pr.Repo)) { remote = fields[i]; remoteUrl = fields[i + 1]; break; }
        if (remote is null) throw new InvalidOperationException("The configured WSL checkout has no remote matching this PR's organization, project and repository. No source worktree was created.");
        var id = Guid.NewGuid().ToString("N");
        var worktree = fields[3] + "/ado-prview-investigations/" + id;
        var source = new InvestigationSource(context, transport, fields[1], fields[2], worktree, id, remote, remoteUrl!, checkAvailable);
        try
        {
            await Execute(transport, source._distro, PrepareScript,
                [source._root, remote, context.Revision.SourceRef, context.Revision.TargetRef, context.Revision.SourceCommit,
                 context.Revision.TargetCommit, context.Revision.BaseCommit, id, worktree, remoteUrl!], ct);
            await source.VerifyAsync(ct);
            return source;
        }
        catch (Exception failure)
        {
            try { await source.DisposeAsync(); }
            catch (Exception cleanup) { throw new InvalidOperationException(failure.Message + " Owned worktree cleanup also failed; retained at " + source.Location + ": " + cleanup.Message, failure); }
            throw;
        }
    }

    async Task<JsonDocument> ReadAsync(string operation, string path, string side, int start, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ExplainContext.ValidatePath(path);
        if (side is not ("before" or "after" or "target") || start < 1)
            throw new ArgumentException("Use before, after or target and a positive starting line.");
        if (Interlocked.Increment(ref _reads) > 120)
        {
            _failure = new InvalidOperationException("This conversation has reached its 120 local source-read limit. Start a new conversation.");
            lock (_evidenceGate) _evidence.Add("Local source-read limit exhausted; further requested content was not inspected.");
            throw _failure;
        }
        var commit = side == "before" ? Context.Revision.BaseCommit : side == "target" ? Context.Revision.TargetCommit : Context.Revision.SourceCommit;
        try
        {
            var text = await Execute(_transport, _distro, ReadScript,
                [_worktree, Context.Revision.SourceCommit, operation, commit, path, start.ToString(System.Globalization.CultureInfo.InvariantCulture)], ct);
            return JsonDocument.Parse(text);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _failure = ex;
            lock (_evidenceGate) _evidence.Add($"Worktree {side}: {path}: read failed; content was not inspected.");
            throw;
        }
    }

    public async Task<string> ReadFileAsync(string path, string side, int startLine, CancellationToken ct)
    {
        using var result = await ReadAsync("file", path, side, startLine, ct);
        var root = result.RootElement;
        if (root.TryGetProperty("omission", out var omission))
        {
            var note = $"{side}: {path}: {omission.GetString()}";
            lock (_evidenceGate) _evidence.Add(note);
            return note;
        }
        var lines = root.GetProperty("lines").EnumerateArray().Select(l => l.GetString()!).ToArray();
        lock (_evidenceGate)
        {
            _windows.Add(new(path, side, startLine, lines));
            _evidence.Add($"Worktree {side}: {path}, lines {startLine}-{startLine + lines.Length - 1}.");
        }
        return $"{path} ({side}), {root.GetProperty("total").GetInt32()} total lines\n" +
            string.Join("\n", lines.Select((line, i) => $"{startLine + i}: {line}"));
    }

    public async Task<string> ListDirectoryAsync(string path, string side, CancellationToken ct)
    {
        using var result = await ReadAsync("directory", path, side, 1, ct);
        lock (_evidenceGate)
        {
            if (result.RootElement.TryGetProperty("omission", out var omission))
                _evidence.Add($"{side}: directory {path}: {omission.GetString()}");
            else _evidence.Add($"Worktree {side}: directory {path} (names only).");
            if (result.RootElement.TryGetProperty("omitted", out var omitted) && omitted.GetInt32() > 0)
                _evidence.Add($"Worktree {side}: directory {path}: {omitted.GetInt32()} entries omitted by the 200-name limit.");
        }
        return result.RootElement.GetRawText();
    }

    public void ValidateFinding(LensFinding finding)
    {
        ExplainContext.ValidatePath(finding.Path);
        if (!Context.Changes.Any(c => c.Path == finding.Path && !c.Folder))
            throw new InvalidDataException("A finding must be attached to a changed source file.");
        var quote = finding.Quote.Replace("\r\n", "\n").Split('\n');
        lock (_evidenceGate)
            if (!_windows.Any(w => w.Path == finding.Path && w.Side == "after" && finding.Line >= w.Start &&
                finding.Line + quote.Length <= w.Start + w.Lines.Length &&
                w.Lines.Skip(finding.Line - w.Start).Take(quote.Length).SequenceEqual(quote)))
                throw new InvalidDataException("A finding's exact quoted lines were not inspected in the pinned source worktree.");
    }

    public async Task VerifyAsync(CancellationToken ct)
    {
        if (_failure is not null) throw new InvalidOperationException("The worktree source could not be fully verified after a failed read. Start a new conversation.", _failure);
        _checkAvailable?.Invoke();
        await Context.VerifyAsync(ct);
        await Execute(_transport, _distro, VerifyScript, [_worktree, Context.Revision.SourceCommit], ct);
        await Execute(_transport, _distro, VerifyRefsScript, [_root, _remote, _remoteUrl, Context.Revision.SourceRef,
            Context.Revision.TargetRef, Context.Revision.SourceCommit, Context.Revision.TargetCommit], ct);
        _checkAvailable?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await Execute(_transport, _distro, CleanupScript, [_root, _worktree, _id, Context.Revision.SourceCommit], timeout.Token);
        _disposed = true;
        Context.Dispose();
    }

    internal const string DiscoverScript = """
        set -euo pipefail
        export GIT_TERMINAL_PROMPT=0 GCM_INTERACTIVE=never
        cwd="$1"; repo="$2"
        [[ -n "$cwd" ]] || cwd="$HOME/clones/$repo"
        if [[ "$cwd" == "~/"* ]]; then cwd="$HOME/${cwd:2}"; fi
        [[ "$cwd" == /* && -d "$cwd" ]] || exit 72
        cd -- "$cwd"
        root="$(git -c core.fsmonitor=false rev-parse --show-toplevel)"
        root="$(cd -- "$root" && pwd -P)"
        command -v python3 >/dev/null
        common="$(git rev-parse --path-format=absolute --git-common-dir)"
        printf 'ADOINSPECT1\0%s\0%s\0%s\0' "${WSL_DISTRO_NAME:?}" "$root" "$common"
        while IFS= read -r remote; do
          [[ -n "$remote" ]] || continue
          printf '%s\0%s\0' "$remote" "$(git remote get-url "$remote")"
        done < <(git remote)
        """;

    internal const string PrepareScript = """
        set -euo pipefail
        export GIT_TERMINAL_PROMPT=0 GCM_INTERACTIVE=never
        root="$1"; remote="$2"; source="$3"; target="$4"; commit="$5"; targetcommit="$6"; base="$7"; id="$8"; work="$9"; remoteurl="${10}"
        [[ "$id" =~ ^[0-9a-f]{32}$ && "$commit" =~ ^[0-9a-f]{40}$ && "$targetcommit" =~ ^[0-9a-f]{40}$ && "$base" =~ ^[0-9a-f]{40}$ ]] || exit 74
        [[ "$source" == refs/heads/* && "$target" == refs/heads/* ]] || exit 74
        git check-ref-format "$source"; git check-ref-format "$target"
        cd -- "$root"
        [[ "$(git remote get-url "$remote")" == "$remoteurl" ]] || exit 74
        parent="$(git rev-parse --path-format=absolute --git-common-dir)/ado-prview-investigations"
        [[ "$work" == "$parent/$id" ]] || exit 74
        mkdir -p -- "$parent"
        [[ -O "$parent" && ! -L "$parent" && ! -e "$work" ]] || exit 74
        exec 9>"$(git rev-parse --git-common-dir)/ado-prview.metadata.lock"
        flock -w 60 9
        git -c core.hooksPath=/dev/null -c core.fsmonitor=false fetch --quiet --no-tags --no-recurse-submodules --no-write-fetch-head -- "$remote" "$source:refs/ado-prview-investigations/$id/source" "$target:refs/ado-prview-investigations/$id/target"
        [[ "$(git rev-parse "refs/ado-prview-investigations/$id/source")" == "$commit" ]] || exit 75
        [[ "$(git rev-parse "refs/ado-prview-investigations/$id/target")" == "$targetcommit" ]] || exit 75
        git cat-file -e "$base^{commit}"
        # No checkout means hostile attributes cannot invoke user-configured smudge filters.
        git -c core.hooksPath=/dev/null -c core.fsmonitor=false worktree add --quiet --detach --no-checkout -- "$work" "$commit"
        """;

    internal const string VerifyScript = """
        set -euo pipefail
        [[ -d "$1" && ! -L "$1" && "$(git -C "$1" -c core.fsmonitor=false rev-parse HEAD)" == "$2" ]] || exit 75
        [[ "$(git -C "$1" rev-parse --show-toplevel)" == "$1" ]] || exit 75
        """;

    internal const string VerifyRefsScript = """
        set -euo pipefail
        export GIT_TERMINAL_PROMPT=0 GCM_INTERACTIVE=never
        cd -- "$1"
        [[ "$(git remote get-url "$2")" == "$3" ]] || exit 75
        refs="$(git -c core.hooksPath=/dev/null ls-remote --refs -- "$2" "$4" "$5")"
        source=""; target=""
        while read -r hash ref; do
          if [[ "$ref" == "$4" ]]; then source="$hash"; fi
          if [[ "$ref" == "$5" ]]; then target="$hash"; fi
        done <<< "$refs"
        [[ "$source" == "$6" && "$target" == "$7" ]] || exit 75
        """;

    internal const string ReadScript = """
        set -euo pipefail
        python3 -I -S - "$@" <<'PY'
        import sys, subprocess, json
        sys.stdout.reconfigure(encoding='utf-8')
        work, head, op, commit, path, start = sys.argv[1:]
        def git(*args):
            return subprocess.check_output(['git', '--no-replace-objects', '-C', work, '-c', 'core.fsmonitor=false', '-c', 'core.hooksPath=/dev/null', *args], stderr=subprocess.DEVNULL)
        assert git('rev-parse', 'HEAD').decode().strip() == head
        assert git('rev-parse', '--show-toplevel').decode().strip() == work
        assert op in ('file','directory') and len(commit)==40 and all(c in '0123456789abcdef' for c in commit)
        assert path.startswith('/') and not path.startswith('//') and '\\' not in path and not any(p in ('.','..') for p in path.split('/'))
        spec = commit + ':' + path.lstrip('/')
        if op == 'directory':
            directory = path.strip('/')
            spec = commit + ':' + directory
            if directory:
                entry = git('ls-tree','-z',commit,'--',':(literal)'+directory).split(b'\0')[0]
                if not entry or not entry.startswith(b'040000 tree '):
                    print(json.dumps({'entries':[], 'omission':'not a tracked directory at the captured revision'})); sys.exit(0)
            assert git('cat-file','-t',spec).strip() == b'tree'
            raw = git('ls-tree','-z',spec)
            assert len(raw) <= 1000000, 'Directory too large'
            entries = raw.decode('utf-8',errors='strict').split('\0')
            entries = [e for e in entries if e]
            print(json.dumps({'entries':entries[:200], 'omitted':max(0,len(entries)-200)}, ensure_ascii=False))
        else:
            # Inspect object type/mode, never dereference symlinks, submodules or worktree files.
            entry = git('ls-tree','-z',commit,'--',':(literal)'+path.lstrip('/')).split(b'\0')[0]
            if not entry or not entry.startswith((b'100644 blob ',b'100755 blob ')):
                print(json.dumps({'omission':'not a regular tracked file (symlinks/submodules are not followed)'})); sys.exit(0)
            size = int(git('cat-file','-s',spec))
            if size > 80000:
                print(json.dumps({'omission':'file exceeds 80,000 bytes; not read'})); sys.exit(0)
            data = git('cat-file','blob',spec)
            if b'\0' in data or data.startswith(b'version https://git-lfs.github.com/spec/v1'):
                print(json.dumps({'omission':'binary or LFS payload not read'})); sys.exit(0)
            lines = data.decode('utf-8',errors='strict').replace('\r\n','\n').split('\n')
            start = int(start); assert 1 <= start <= len(lines)
            window = lines[start-1:start+119]
            if sum(len(l) for l in window) > 16000:
                print(json.dumps({'omission':'requested window exceeds 16,000 characters; choose another starting line or file'})); sys.exit(0)
            print(json.dumps({'lines':window,'total':len(lines)}, ensure_ascii=False))
        PY
        """;

    internal const string CleanupScript = """
        set -euo pipefail
        root="$1"; work="$2"; id="$3"; commit="$4"
        [[ "$id" =~ ^[0-9a-f]{32}$ ]] || exit 77
        cd -- "$root"
        common="$(git rev-parse --path-format=absolute --git-common-dir)"
        [[ "$work" == "$common/ado-prview-investigations/$id" ]] || exit 77
        exec 9>"$(git rev-parse --git-common-dir)/ado-prview.metadata.lock"
        flock -w 60 9
        if [[ -e "$work" ]]; then
          [[ ! -L "$work" && -f "$work/.git" && ! -L "$work/.git" && "$(git -C "$work" rev-parse HEAD)" == "$commit" ]] || exit 77
          [[ "$(git -C "$work" rev-parse --path-format=absolute --git-common-dir)" == "$common" && "$(git -C "$work" rev-parse --show-toplevel)" == "$work" ]] || exit 77
          [[ "$(git -C "$work" symbolic-ref -q HEAD || true)" == "" ]] || exit 77
          [[ "$(git -C "$work" -c core.fsmonitor=false ls-files --stage)" == "" ]] || exit 77
          # A no-checkout worktree has an intentionally empty index; never force removal of user-added files.
          [[ "$(find "$work" -mindepth 1 -maxdepth 1 ! -name .git -print -quit)" == "" ]] || exit 77
          git -c core.hooksPath=/dev/null -c core.fsmonitor=false worktree remove --force -- "$work"
        fi
        git -c core.hooksPath=/dev/null -c core.fsmonitor=false update-ref -d "refs/ado-prview-investigations/$id/source"
        git -c core.hooksPath=/dev/null -c core.fsmonitor=false update-ref -d "refs/ado-prview-investigations/$id/target"
        """;
}
