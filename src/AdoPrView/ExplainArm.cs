using System.Text;
using System.Text.RegularExpressions;
using DiffPlex;
using System.Collections.Concurrent;

namespace AdoPrView;

internal sealed class ExplainArm
{
    sealed record Source(ExplainChange Change, string? Before, string? After);
    readonly ConcurrentDictionary<int, Source> _sources = new();
    readonly ConcurrentDictionary<int, string> _sections = new();
    readonly ConcurrentDictionary<string, string> _context = new(StringComparer.Ordinal);

    internal static bool Candidate(ExplainChange change, string? before = null, string? after = null)
    {
        if (change.Path.EndsWith(".bicep", StringComparison.OrdinalIgnoreCase) ||
            change.OriginalPath?.EndsWith(".bicep", StringComparison.OrdinalIgnoreCase) == true) return true;
        var text = (before ?? "") + "\n" + (after ?? "");
        if (text.Contains("schema.management.azure.com/schemas/", StringComparison.OrdinalIgnoreCase)) return true;
        var oldLines = (before ?? "").Split('\n').ToHashSet(StringComparer.Ordinal);
        var newLines = (after ?? "").Split('\n').ToHashSet(StringComparer.Ordinal);
        var changed = string.Join("\n", oldLines.Except(newLines).Concat(newLines.Except(oldLines)));
        return changed.Contains("x-ms-azure-resource", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(changed, @"/providers/Microsoft\.[\w.]+/", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(changed, @"(?im)^\s*resource\s+\w+\s+'Microsoft\.[^']+@\d{4}-\d{2}-\d{2}");
    }

    internal void Observe(ExplainChange change, string? before, string? after) =>
        _sources[change.Index] = new(change, before, after);

    internal void ObserveFile(string path, string side, int startLine, string text)
    {
        if (side is "before" or "after") _context[$"{side}:{path}:{startLine}"] = text;
    }

    internal string Record(int index, string beforeKind, string before, string afterKind, string after,
        string improvement, string risks, string difference)
    {
        if (!_sources.TryGetValue(index, out var source))
            throw new ArgumentException("Read this change's pinned diff before recording its ARM explanation.");
        if (string.IsNullOrWhiteSpace(improvement) || string.IsNullOrWhiteSpace(risks) || string.IsNullOrWhiteSpace(difference))
            throw new ArgumentException("Supply an evidenced improvement, risks/uncertainty and a plain-language description of the changed lines.");
        var b = Example(source, true, beforeKind, before);
        var a = Example(source, false, afterKind, after);
        var comparison = "";
        if (beforeKind is "source" or "illustrative" or "absent" && afterKind is "source" or "illustrative" or "absent")
        {
            var oldCode = beforeKind == "absent" ? "" : before;
            var newCode = afterKind == "absent" ? "" : after;
            if (oldCode.Trim() == newCode.Trim())
                throw new ArgumentException("Before/After examples are identical. Show the deployment-facing change or explain why that side cannot be represented.");
            var diff = new Differ().CreateLineDiffs(oldCode, newCode, false);
            var lines = diff.DiffBlocks.SelectMany(block =>
                diff.PiecesOld.Skip(block.DeleteStartA).Take(block.DeleteCountA).Select(line => "-" + line)
                    .Concat(diff.PiecesNew.Skip(block.InsertStartB).Take(block.InsertCountB).Select(line => "+" + line)));
            comparison = "\n\n#### Changed lines (- removed, + added)\n\n" + Fence(string.Join("\n", lines), "diff");
        }
        _sections[index] = $"### {Inline(source.Change.Path)}\n\n#### Before\n\n{b}\n\n#### After\n\n{a}\n\n" +
            $"**What changed:** {Inline(difference)}{comparison}\n\n**Improvement:** {Inline(improvement)}\n\n**Risks and uncertainty:** {Inline(risks)}";
        return "ARM examples recorded for this change. The host appends the labeled Before/After, changed-lines description, improvement and risks to the final explanation; do not duplicate them.";
    }

    string Example(Source source, bool isBefore, string kind, string code)
    {
        var text = isBefore ? source.Before : source.After;
        var absent = source.Change.Kind.Split(',').Any(k => k.Trim().Equals(isBefore ? "add" : "delete", StringComparison.OrdinalIgnoreCase));
        if (kind == "absent" && absent) return "Not present in this revision (resource/file " + (isBefore ? "added" : "deleted") + ").";
        if (kind == "unavailable" && !string.IsNullOrWhiteSpace(code))
            return "Bicep example unavailable: " + Inline(code) + " No deployment validation was performed.";
        if (kind is not ("source" or "illustrative") || absent || text is null || string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("Use source/illustrative with inspected evidence, absent only for an added/deleted file, or unavailable with an honest reason.");
        if (kind == "source")
        {
            var path = isBefore ? source.Change.OriginalPath ?? source.Change.Path : source.Change.Path;
            if (!path.EndsWith(".bicep", StringComparison.OrdinalIgnoreCase) ||
                !text.Replace("\r\n", "\n").Contains(code.Replace("\r\n", "\n").Trim(), StringComparison.Ordinal))
                throw new ArgumentException("An exact-source Bicep example must be a contiguous excerpt of the inspected Bicep side; use illustrative for derived API/template examples.");
        }
        else
        {
            var side = isBefore ? "before:" : "after:";
            text += "\n" + string.Join("\n", _context.Where(p => p.Key.StartsWith(side, StringComparison.Ordinal)).Select(p => p.Value));
            var resources = Regex.Matches(code, @"\bresource\s+\w+\s+'([^'@]+)@([^']+)'");
            if (resources.Count == 0) throw new ArgumentException("Derived Bicep needs an evidenced resource type and API version; otherwise use unavailable.");
            foreach (Match resource in resources)
                foreach (var value in new[] { resource.Groups[1].Value, resource.Groups[2].Value })
                    if (!HasToken(text, value))
                        throw new ArgumentException("The derived Bicep resource type/API version is not in this side's source evidence; read more context or use unavailable.");
            foreach (Match property in Regex.Matches(code, @"(?m)(?:^|[{,])\s*(?:'([^']+)'|(\w+))\s*:"))
                if (!HasToken(text, property.Groups[1].Success ? property.Groups[1].Value : property.Groups[2].Value))
                    throw new ArgumentException("A derived Bicep property is not present in this side's source evidence; omit it or report unavailable.");
            var body = Regex.Replace(code, @"\bresource\s+\w+\s+'[^']+'", "");
            foreach (Match literal in Regex.Matches(body, @"'([^']*)'|\b\d+(?:\.\d+)?\b|\b(?:true|false)\b"))
            {
                var value = literal.Groups[1].Success ? literal.Groups[1].Value : literal.Value;
                if (!HasToken(text, value))
                    throw new ArgumentException("A derived Bicep value is not in this side's evidence; use only evidenced values or report unavailable.");
            }
        }
        return (kind == "source" ? "Exact source excerpt" : "Illustrative Bicep derived from inspected ARM evidence; not Bicep from the PR") +
            " (not deployment-validated):\n\n" + Fence(code, "bicep");
    }

    static string Fence(string code, string language)
    {
        var fence = new string('`', Math.Max(3, Regex.Matches(code, "`+").Select(m => m.Length + 1).DefaultIfEmpty(3).Max()));
        return fence + language + "\n" + code.TrimEnd() + "\n" + fence;
    }

    static bool HasToken(string text, string value) =>
        Regex.IsMatch(text, @"(?<![\w.-])" + Regex.Escape(value) + @"(?![\w.-])", RegexOptions.IgnoreCase);

    static string Inline(string value) => System.Net.WebUtility.HtmlEncode(value).Replace("\r", "").Replace("\n", " ");

    internal string Complete(string output, IReadOnlyList<ExplainChange> changes)
    {
        var missing = changes.Where(c => !_sections.ContainsKey(c.Index) &&
            (Candidate(c) || _sources.TryGetValue(c.Index, out var source) && Candidate(c, source.Before, source.After))).ToArray();
        if (missing.Length != 0)
            throw new InvalidOperationException("ARM explanation missing grounded Before/After examples and assessment for: " +
                string.Join(", ", missing.Select(c => c.Path)) + ". Use explain_arm_change after reading each diff; use unavailable explicitly when evidence is insufficient.");
        if (_sections.Count == 0) return output;
        var builder = new StringBuilder(output.TrimEnd()).Append("\n\n## Azure Resource Manager changes\n\n");
        builder.AppendJoin("\n\n", _sections.OrderBy(p => p.Key).Select(p => p.Value));
        return builder.ToString();
    }
}
