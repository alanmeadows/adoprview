using System.IO;
using System.Net;

namespace AdoPrView;

internal sealed class AdoInvestigationSource(ExplainContext context, Action? checkAvailable = null) : IInvestigationSource
{
    readonly List<SourceWindow> _windows = [];
    readonly List<string> _evidence = [];
    readonly object _gate = new();
    bool _disposed;
    public ExplainContext Context => context;
    public string Location => "Azure DevOps: authenticated read-only source at the captured base, source and target commits (no local checkout or repository execution)";
    public string[] Evidence { get { lock (_gate) return context.Evidence().Concat(_evidence).ToArray(); } }

    public async Task<string> ReadFileAsync(string path, string side, int startLine, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try { return await context.ReadFileWindowAsync(path, side, startLine, ct, window =>
        {
            lock (_gate)
            {
                _windows.Add(window);
                _evidence.Add($"Source {side}: {path}, lines {startLine}-{startLine + window.Lines.Length - 1} (pinned ADO content).");
            }
        }); }
        catch (AdoException ex) when (ex.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        { throw SourceAccessDenied(ex); }
    }

    public async Task<string> ListDirectoryAsync(string path, string side, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try { return await context.ListDirectoryAsync(path, side, ct, investigation: true); }
        catch (AdoException ex) when (ex.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        { throw SourceAccessDenied(ex); }
    }

    static InvalidOperationException SourceAccessDenied(AdoException ex) => new(
        "Azure DevOps source access was denied. Check this repository's Read permission and the dashboard's ADO sign-in " +
        "(Windows 'az login', or your existing configured ADO credential). Copilot/GitHub sign-in does not grant ADO access. The requested source content was not inspected; this read posted no comments.", ex);

    public void ValidateFinding(LensFinding finding)
    {
        ExplainContext.ValidatePath(finding.Path);
        if (finding.Line < 1 || !context.Changes.Any(c => c.Path == finding.Path && !c.Folder))
            throw new InvalidDataException("A finding must be attached to a changed source file at a positive line.");
        var quote = finding.Quote.Replace("\r\n", "\n").Split('\n');
        lock (_gate)
            if (!_windows.Any(w => w.Path == finding.Path && w.Side == "after" && finding.Line >= w.Start &&
                finding.Line + quote.Length <= w.Start + w.Lines.Length &&
                w.Lines.Skip(finding.Line - w.Start).Take(quote.Length).SequenceEqual(quote)))
                throw new InvalidDataException("A finding's exact quoted lines were not inspected in the pinned source.");
    }

    public async Task VerifyAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        checkAvailable?.Invoke();
        await context.VerifyAsync(ct);
        checkAvailable?.Invoke();
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed) { _disposed = true; context.Dispose(); }
        return ValueTask.CompletedTask;
    }
}
