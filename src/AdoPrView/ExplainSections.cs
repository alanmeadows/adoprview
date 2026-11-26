using System.Text.RegularExpressions;

namespace AdoPrView;

internal sealed record ExplainSection(int Index, string Title, int Characters, bool IncludesArm);

internal sealed class ExplainSections
{
    sealed record Heading(int Start, int Level, string Title);
    readonly string _output;
    readonly Heading[] _headings;
    readonly int? _armStart;
    public ExplainSection[] Metadata { get; }

    ExplainSections(string output, Heading[] headings, int? armStart)
    {
        _output = output;
        _headings = headings;
        _armStart = armStart;
        Metadata = headings.Select((h, i) => new ExplainSection(i, h.Title, Body(i).Length, armStart is not null)).ToArray();
    }

    public static ExplainSections Create(string output, int? armStart)
    {
        var headings = new List<Heading>();
        char fence = '\0';
        var fenceLength = 0;
        foreach (Match line in Regex.Matches(output, @"[^\n]*(?:\n|$)"))
        {
            if (line.Length == 0) continue;
            var text = line.Value.TrimEnd('\r', '\n');
            var marker = Regex.Match(text, @"^ {0,3}(`{3,}|~{3,})(.*)$");
            if (fence != '\0')
            {
                if (marker.Success && marker.Groups[1].Value[0] == fence &&
                    marker.Groups[1].Length >= fenceLength && string.IsNullOrWhiteSpace(marker.Groups[2].Value))
                    fence = '\0';
                continue;
            }
            if (marker.Success)
            {
                fence = marker.Groups[1].Value[0];
                fenceLength = marker.Groups[1].Length;
                continue;
            }
            var heading = Regex.Match(text, @"^ {0,3}(#{1,6})[ \t]+(.+?)[ \t]*$");
            if (!heading.Success || armStart is int arm && line.Index > arm) continue;
            var title = Regex.Replace(heading.Groups[2].Value, @"[ \t]+#+[ \t]*$", "");
            headings.Add(new(line.Index, heading.Groups[1].Length, title.Length > 200 ? title[..200] : title));
        }
        // An unclosed fence or an excessively fragmented document has no safe bounded section menu.
        if (fence != '\0' || headings.Count > 64) headings.Clear();
        return new(output, headings.ToArray(), armStart);
    }

    (int Start, int End) Range(int index)
    {
        if (index < 0 || index >= _headings.Length)
            throw new InvalidOperationException("The selected explanation section is unavailable or invalid. Select a server-listed section or the entire explanation.");
        var heading = _headings[index];
        var next = _headings.Skip(index + 1).FirstOrDefault(h => h.Level <= heading.Level);
        return (heading.Start, next?.Start ?? _output.Length);
    }

    string Body(int index)
    {
        var (start, end) = Range(index);
        var body = _output[start..end];
        if (_armStart is int arm && !(start <= arm && end == _output.Length))
            body += "\n\n" + _output[arm..];
        return body;
    }

    public ExplanationAttachment Attach(ExplainState state, int index)
    {
        var (start, end) = Range(index);
        bool Included(Heading heading) => heading.Start >= start && heading.Start < end ||
            _armStart is int arm && heading.Start >= arm;
        var included = _headings.Select((h, i) => (h, i)).Where(pair => Included(pair.h)).Select(pair => pair.i).ToArray();
        var omitted = _headings.Where(h => !Included(h)).Select(h => h.Title).ToList();
        if (_headings.Length != 0 && _headings[0].Start > 0) omitted.Insert(0, "Preamble before the first heading");
        var receipt = $"Explicit section {index}: {_headings[index].Title}. " +
            (_armStart is null ? "" : "The complete host ARM section (Before/After, attribution, changed lines, improvement and risks) is also included. ") +
            (omitted.Count == 0 ? "No other heading-bound sections omitted." : "Omitted sections: " + string.Join("; ", omitted) + ".");
        return new(state.RequestId, Body(index), state.Revision!, [.. state.Evidence ?? []], [.. state.Coverage],
            index, _headings[index].Title, included, omitted.ToArray(), receipt);
    }
}
