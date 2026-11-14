using System.Text;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DiffPlex;

namespace AdoPrView;

internal sealed record ExplainRevision(string Key, int Id, string Title, string Repo, string SourceCommit, string BaseCommit, int Iteration);
internal sealed record ExplainChange(int Index, string Path, string? OriginalPath, string Kind, bool Folder);

internal sealed class ExplainContext : IDisposable
{
    const int MaxChanges = 1000;
    const int MaxFileCharacters = 80_000;
    readonly AdoClient _ado;
    readonly PrView _pr;
    readonly string _repoApi;
    readonly string _prApi;
    readonly string _sourceRef;
    readonly string _targetRef;
    readonly List<ExplainChange> _changes;
    readonly HashSet<int> _inspected = [];
    readonly HashSet<string> _omissions = new(StringComparer.Ordinal);
    readonly HashSet<string> _evidence = new(StringComparer.Ordinal);
    readonly SemaphoreSlim _reads = new(1, 1);
    readonly object _coverageGate = new();
    Exception? _toolFailure;
    int _fileReads;

    public ExplainRevision Revision { get; }
    public IReadOnlyList<ExplainChange> Changes => _changes;
    public int DiffReads { get; private set; }
    public int ContextReads { get; private set; }

    ExplainContext(AdoClient ado, PrView pr, string repoApi, string sourceRef, string targetRef,
        ExplainRevision revision, List<ExplainChange> changes, bool capped)
    {
        _ado = ado;
        _pr = pr;
        _repoApi = repoApi;
        _prApi = repoApi + "/pullrequests/" + pr.Id;
        _sourceRef = sourceRef;
        _targetRef = targetRef;
        Revision = revision;
        _changes = changes;
        if (capped) _omissions.Add($"Change inventory limited to {MaxChanges} entries; additional changes were not inspected.");
    }

    static string S(JsonNode? n) => n?.GetValue<string>() ?? "";
    static string Commit(JsonNode? n)
    {
        var value = S(n?["commitId"]);
        if (!Regex.IsMatch(value, @"\A[a-fA-F0-9]{40}\z"))
            throw new InvalidDataException("ADO did not return a valid pinned commit.");
        return value;
    }

    static void CheckIdentity(JsonNode fresh, PrView pr)
    {
        if (fresh["pullRequestId"]?.GetValue<int>() != pr.Id ||
            !S(fresh["repository"]?["id"]).Equals(pr.RepoId, StringComparison.OrdinalIgnoreCase) ||
            !S(fresh["repository"]?["project"]?["id"]).Equals(pr.ProjectId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ADO did not confirm the selected PR identity.");
        var fork = S(fresh["forkSource"]?["repository"]?["id"]);
        if (fork.Length != 0 && !fork.Equals(pr.RepoId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Explaining a cross-repository fork is not supported yet; no source content was read.");
    }

    public static async Task<ExplainContext> CaptureAsync(AdoClient ado, PrView pr, CancellationToken ct)
    {
        if (!Regex.IsMatch(pr.Org, @"\A[a-zA-Z0-9][a-zA-Z0-9-]{0,49}\z") ||
            !Guid.TryParse(pr.ProjectId, out _) || !Guid.TryParse(pr.RepoId, out _) || pr.Id <= 0)
            throw new ArgumentException("The selected PR has no verified ADO repository identity.");
        var repoApi = $"https://dev.azure.com/{pr.Org}/{pr.ProjectId}/_apis/git/repositories/{pr.RepoId}";
        var prApi = repoApi + "/pullrequests/" + pr.Id;
        var fresh = await ado.GetAsync(prApi + "?api-version=7.1", ct);
        CheckIdentity(fresh, pr);
        var sourceRef = S(fresh["sourceRefName"]);
        var targetRef = S(fresh["targetRefName"]);
        if (!sourceRef.StartsWith("refs/heads/", StringComparison.Ordinal) || !targetRef.StartsWith("refs/heads/", StringComparison.Ordinal))
            throw new InvalidDataException("ADO did not return the PR's source and target branches.");
        var iterations = await ado.GetAsync(prApi + "/iterations?api-version=7.1", ct);
        var latest = (iterations["value"] as JsonArray)?.OrderByDescending(n => n?["id"]?.GetValue<int>()).FirstOrDefault()
            ?? throw new InvalidDataException("ADO returned no PR iterations.");
        var id = latest["id"]?.GetValue<int>() ?? 0;
        if (id <= 0) throw new InvalidDataException("ADO returned an invalid PR iteration.");
        var source = Commit(latest["sourceRefCommit"]);
        var common = Commit(latest["commonRefCommit"]);
        if (fresh["lastMergeSourceCommit"] is { } preview && Commit(preview) != source)
            throw new InvalidOperationException("The PR changed while ADO was updating its preview. Refresh and retry.");
        var changes = new List<ExplainChange>();
        var skip = 0;
        bool more;
        do
        {
            var page = await ado.GetBoundedJsonAsync($"{prApi}/iterations/{id}/changes?$compareTo=0&$top=200&$skip={skip}&api-version=7.1", 2_000_000, ct);
            var entries = page["changeEntries"] as JsonArray ?? throw new InvalidDataException("ADO returned no change inventory.");
            var remaining = MaxChanges - changes.Count;
            foreach (var entry in entries)
            {
                if (entry is null) throw new InvalidDataException("ADO returned an empty change entry.");
                var path = ValidatePath(S(entry["item"]?["path"]));
                var original = S(entry["originalPath"]);
                if (original.Length > 0) original = ValidatePath(original);
                changes.Add(new(changes.Count, path, original.Length == 0 ? null : original, S(entry["changeType"]),
                    entry["item"]?["isFolder"]?.GetValue<bool>() == true || S(entry["item"]?["gitObjectType"]) == "tree"));
                if (changes.Count == MaxChanges) break;
            }
            var next = page["nextSkip"]?.GetValue<int>() ?? 0;
            if (next > 0 && (next <= skip || entries.Count == 0))
                throw new InvalidDataException("ADO change pagination did not advance.");
            more = next > 0 || entries.Count > remaining;
            skip = next;
        } while (more && changes.Count < MaxChanges);
        var context = new ExplainContext(ado, pr, repoApi, sourceRef, targetRef,
            new(pr.Key, pr.Id, S(fresh["title"]), pr.Repo, source, common, id), changes, more);
        try { await context.VerifyAsync(ct); return context; }
        catch { context.Dispose(); throw; }
    }

    public async Task VerifyAsync(CancellationToken ct)
    {
        if (_toolFailure is { } failure)
            throw new InvalidOperationException("PR context could not be verified: " + failure.Message, failure);
        var fresh = await _ado.GetAsync(_prApi + "?api-version=7.1", ct);
        CheckIdentity(fresh, _pr);
        var iterations = await _ado.GetAsync(_prApi + "/iterations?api-version=7.1", ct);
        var iteration = (iterations["value"] as JsonArray)?.OrderByDescending(n => n?["id"]?.GetValue<int>()).FirstOrDefault()
            ?? throw new InvalidDataException("ADO returned no PR iterations during verification.");
        if (iteration["id"]?.GetValue<int>() != Revision.Iteration ||
            S(fresh["sourceRefName"]) != _sourceRef || S(fresh["targetRefName"]) != _targetRef ||
            (fresh["lastMergeSourceCommit"] is { } source && Commit(source) != Revision.SourceCommit) ||
            Commit(iteration["sourceRefCommit"]) != Revision.SourceCommit || Commit(iteration["commonRefCommit"]) != Revision.BaseCommit)
            throw new InvalidOperationException("The PR revisions changed during explanation. The partial result is for the displayed pinned commits; refresh and explain again.");
    }

    public string ListChanges(int page)
    {
        if (page < 0 || page > _changes.Count / 50) throw new ArgumentOutOfRangeException(nameof(page));
        return JsonSerializer.Serialize(new { Revision, changes = _changes.Skip(page * 50).Take(50),
            nextPage = (page + 1) * 50 < _changes.Count ? page + 1 : (int?)null, totalInventoried = _changes.Count, omissions = Coverage() }, Bridge.Json);
    }

    public async Task<string> ReadChangeAsync(int index, CancellationToken ct)
    {
        if (index < 0 || index >= _changes.Count) throw new ArgumentOutOfRangeException(nameof(index));
        await _reads.WaitAsync(ct);
        try
        {
            var change = _changes[index];
            DiffReads++;
            if (change.Folder) return Omit(change.Path, "folder/submodule entry; no textual diff");
            var kinds = change.Kind.Split(',').Select(s => s.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var before = kinds.Contains("add") ? "" : await ReadTextAsync(change.OriginalPath ?? change.Path, "before", ct);
            var after = kinds.Contains("delete") ? "" : await ReadTextAsync(change.Path, "after", ct);
            if (before is null || after is null) return $"No complete text diff for {change.Path}. " + string.Join(" ", Coverage());
            var diff = new Differ().CreateLineDiffs(before, after, false);
            var output = new StringBuilder($"--- {change.OriginalPath ?? change.Path} @ {Revision.BaseCommit}\n+++ {change.Path} @ {Revision.SourceCommit}\n");
            foreach (var block in diff.DiffBlocks)
            {
                output.AppendLine($"@@ before line {block.DeleteStartA + 1}, after line {block.InsertStartB + 1} @@");
                for (var i = Math.Max(0, block.DeleteStartA - 3); i < block.DeleteStartA; i++) output.AppendLine(" " + diff.PiecesOld[i]);
                for (var i = block.DeleteStartA; i < block.DeleteStartA + block.DeleteCountA; i++) output.AppendLine("-" + diff.PiecesOld[i]);
                for (var i = block.InsertStartB; i < block.InsertStartB + block.InsertCountB; i++) output.AppendLine("+" + diff.PiecesNew[i]);
                for (var i = block.InsertStartB + block.InsertCountB; i < Math.Min(diff.PiecesNew.Count, block.InsertStartB + block.InsertCountB + 3); i++)
                    output.AppendLine(" " + diff.PiecesNew[i]);
            }
            if (!diff.DiffBlocks.Any()) output.AppendLine("No textual change (possibly a rename or metadata-only change).");
            if (output.Length > 24_000)
            {
                Omit(change.Path, "diff truncated after 24,000 characters; use read_pr_file for targeted context");
                return output.ToString(0, 24_000) + "\n[DIFF TRUNCATED]";
            }
            lock (_coverageGate) _inspected.Add(index);
            NoteEvidence($"Diff: {change.OriginalPath ?? change.Path} -> {change.Path}.");
            return output.ToString();
        }
        catch (Exception ex) when (ex is not ArgumentException && !ct.IsCancellationRequested)
        {
            _toolFailure = ex;
            Omit(_changes[index].Path, "context read failed: " + ex.Message);
            throw;
        }
        finally { _reads.Release(); }
    }

    public async Task<string> ReadFileAsync(string path, string side, int startLine, CancellationToken ct)
    {
        ValidatePath(path);
        if (side is not ("before" or "after") || startLine < 1) throw new ArgumentException("Use before/after and a positive starting line.");
        await _reads.WaitAsync(ct);
        try
        {
            var text = await ReadTextAsync(path, side, ct);
            if (text is null) return $"File omitted: {path}. " + string.Join(" ", Coverage());
            var lines = text.Replace("\r\n", "\n").Split('\n');
            if (startLine > lines.Length) throw new ArgumentException("Starting line is beyond the file.");
            ContextReads++;
            NoteEvidence($"{side}: {path}, lines {startLine}-{Math.Min(lines.Length, startLine + 119)}.");
            var output = string.Join("\n", lines.Skip(startLine - 1).Take(120).Select((line, i) => $"{startLine + i}: {line}"));
            if (output.Length > 16_000)
            {
                Omit(path, "context window truncated at 16,000 characters");
                output = output[..16_000] + "\n[WINDOW TRUNCATED]";
            }
            return $"{path} @ {(side == "before" ? Revision.BaseCommit : Revision.SourceCommit)}; {lines.Length} total lines; window starts at {startLine}\n{output}";
        }
        catch (Exception ex) when (ex is not ArgumentException && !ct.IsCancellationRequested)
        {
            _toolFailure = ex;
            Omit(path, "context read failed: " + ex.Message);
            throw;
        }
        finally { _reads.Release(); }
    }

    public async Task<string> ListDirectoryAsync(string path, string side, CancellationToken ct)
    {
        ValidatePath(path);
        if (side is not ("before" or "after")) throw new ArgumentException("Use before or after.");
        await _reads.WaitAsync(ct);
        try
        {
            if (++_fileReads > 60) return Omit(path, "60 file/directory-read budget exhausted");
            var commit = side == "before" ? Revision.BaseCommit : Revision.SourceCommit;
            var url = $"{_repoApi}/items?scopePath={Uri.EscapeDataString(path)}&recursionLevel=OneLevel&versionDescriptor.versionType=commit&versionDescriptor.version={commit}&api-version=7.1";
            var response = await _ado.GetBoundedJsonAsync(url, 1_000_000, ct);
            var items = response["value"] as JsonArray ?? throw new InvalidDataException("ADO returned no directory inventory.");
            var entries = items.Select(item =>
            {
                var itemPath = ValidatePath(S(item?["path"]));
                var prefix = path.TrimEnd('/') + "/";
                if (S(item?["commitId"]) != commit ||
                    itemPath != path && (!itemPath.StartsWith(prefix, StringComparison.Ordinal) || itemPath[prefix.Length..].Contains('/')))
                    throw new InvalidDataException("ADO returned a directory entry outside the pinned scope.");
                return new { path = itemPath, folder = item?["isFolder"]?.GetValue<bool>() == true };
            }).Where(item => item.path != path).OrderBy(item => item.path, StringComparer.Ordinal).ToArray();
            if (entries.Length > 200) Omit(path, "directory listing limited to 200 entries; additional names omitted");
            NoteEvidence($"{side}: directory {path} ({Math.Min(entries.Length, 200)} entries).");
            return JsonSerializer.Serialize(new { path, commit, entries = entries.Take(200), total = entries.Length }, Bridge.Json);
        }
        catch (AdoException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound)
        { return Omit(path, $"{side}: directory not found at pinned commit"); }
        catch (Exception ex) when (ex is not ArgumentException && !ct.IsCancellationRequested)
        {
            _toolFailure = ex;
            Omit(path, "directory read failed: " + ex.Message);
            throw;
        }
        finally { _reads.Release(); }
    }

    void NoteEvidence(string note) { lock (_coverageGate) _evidence.Add(note); }
    public string[] Evidence() { lock (_coverageGate) return _evidence.ToArray(); }

    async Task<string?> ReadTextAsync(string path, string side, CancellationToken ct)
    {
        if (++_fileReads > 60) { Omit(path, "60 file/directory-read budget exhausted"); return null; }
        var commit = side == "before" ? Revision.BaseCommit : Revision.SourceCommit;
        var url = $"{_repoApi}/items?path={Uri.EscapeDataString(ValidatePath(path))}&versionDescriptor.versionType=commit&versionDescriptor.version={commit}&includeContentMetadata=true&api-version=7.1";
        try
        {
            var metadata = await _ado.GetBoundedJsonAsync(url, 16_000, ct);
            if (S(metadata["path"]) != path || S(metadata["commitId"]) != commit)
                throw new InvalidDataException("ADO returned content for a different path or revision.");
            if (metadata["contentMetadata"]?["isBinary"]?.GetValue<bool>() == true ||
                metadata["isFolder"]?.GetValue<bool>() == true || S(metadata["gitObjectType"]) is "tree" or "commit")
            { Omit(path, $"{side}: binary, folder or submodule; text not inspected"); return null; }
            var content = await _ado.GetBoundedJsonAsync(url + "&includeContent=true", 512_000, ct);
            if (S(content["path"]) != path || S(content["commitId"]) != commit)
                throw new InvalidDataException("ADO returned content for a different path or revision.");
            var text = content["content"]?.GetValue<string>();
            if (text is null || text.Length > MaxFileCharacters || text.Contains('\0'))
            { Omit(path, $"{side}: unavailable, binary or larger than {MaxFileCharacters:N0} characters"); return null; }
            if (text.StartsWith("version https://git-lfs.github.com/spec/v1", StringComparison.Ordinal))
            { Omit(path, $"{side}: Git LFS pointer; binary payload not inspected"); return null; }
            return text;
        }
        catch (AdoException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound)
        { Omit(path, $"{side}: file not found at pinned commit"); return null; }
        catch (InvalidDataException ex) when (ex.Message == "ADO response exceeds the explanation size limit.")
        { Omit(path, $"{side}: response exceeds size limit"); return null; }
    }

    string Omit(string path, string reason)
    {
        var note = $"{path}: {reason}.";
        lock (_coverageGate) _omissions.Add(note);
        return note;
    }

    public string[] Coverage()
    {
        lock (_coverageGate)
        {
            var unread = _changes.Where(c => !_inspected.Contains(c.Index)).ToList();
            return _omissions.Concat(unread.Count == 0 ? [] : new[]
            {
                $"{unread.Count} inventoried changes have not had a complete text diff inspected: {string.Join(", ", unread.Take(8).Select(c => c.Path))}{(unread.Count > 8 ? ", ..." : "")}."
            }).ToArray();
        }
    }

    internal static string ValidatePath(string path)
    {
        if (!path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal) || path.Length > 2048 ||
            path.Contains('\\') || path.Any(char.IsControl) || path.Split('/').Any(s => s is ".." or ".") || path.Contains('?') || path.Contains('#'))
            throw new ArgumentException("Use an absolute repository path without traversal, query or fragment.");
        return path;
    }

    public void Dispose() => _reads.Dispose();
}
