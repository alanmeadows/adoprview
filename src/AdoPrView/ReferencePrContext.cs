using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AdoPrView;

internal sealed record ReferencePrAttachment(string Url, ExplainRevision Revision, string Output, string[] Evidence, string[] Coverage);

internal sealed class ReferencePrContext(string url, ExplainContext context, string output) : IDisposable
{
    public string Url { get; } = url;
    public ExplainContext Context { get; } = context;
    public ReferencePrAttachment Snapshot() => new(Url, Context.Revision, output, Context.Evidence(), Context.Coverage());
    public void Dispose() => Context.Dispose();

    internal static string[] QuestionUrls(string question) => Regex.Matches(question, @"\b[a-z][a-z0-9+.-]*://[^\s<>""`]+", RegexOptions.IgnoreCase)
        .Select(m => Canonical(m.Value.TrimEnd('.', ',', ';', ':', ')', ']', '}', '\''))).Distinct(StringComparer.Ordinal).ToArray();

    internal static string Canonical(string value)
    {
        var (org, project, repo, id) = Parse(value);
        return $"https://dev.azure.com/{Uri.EscapeDataString(org)}/{Uri.EscapeDataString(project)}/_git/{Uri.EscapeDataString(repo)}/pullrequest/{id}";
    }

    static (string Org, string Project, string Repo, int Id) Parse(string value)
    {
        if (value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || value.Contains('\\'))
            throw new ArgumentException("Unsupported reference URL. Use a canonical HTTPS Azure DevOps PR URL without query, fragment or credentials.");
        var parts = uri.AbsolutePath.Trim('/').Split('/').Select(Uri.UnescapeDataString).ToArray();
        string org;
        if (uri.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase) && parts.Length == 6)
        { org = parts[0]; parts = parts[1..]; }
        else if (uri.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase))
        {
            org = uri.Host[..^".visualstudio.com".Length];
            if (parts.Length == 6 && parts[0] == "DefaultCollection") parts = parts[1..];
        }
        else throw new ArgumentException("Unsupported reference URL host. Only Azure DevOps PR URLs are read; arbitrary URLs are not fetched.");
        if (!Regex.IsMatch(org, @"\A[a-zA-Z0-9][a-zA-Z0-9-]{0,49}\z") || parts.Length != 5 ||
            parts[1] != "_git" || parts[3] != "pullrequest" ||
            parts.Any(s => string.IsNullOrWhiteSpace(s) || s is "." or ".." || s.IndexOfAny(['/', '\\', '?', '#', '%']) >= 0 || s.Any(char.IsControl)) ||
            !int.TryParse(parts[4], out var id) || id <= 0)
            throw new ArgumentException("Unsupported reference URL path. Use https://dev.azure.com/org/project/_git/repo/pullrequest/N.");
        return (org.ToLowerInvariant(), parts[0], parts[2], id);
    }

    public static async Task<ReferencePrContext> CaptureAsync(AdoClient ado, string value, CancellationToken ct)
    {
        var url = Canonical(value);
        var (org, project, repo, id) = Parse(url);
        ExplainContext? context = null;
        try
        {
            var api = $"https://dev.azure.com/{Uri.EscapeDataString(org)}/{Uri.EscapeDataString(project)}/_apis/git/repositories/{Uri.EscapeDataString(repo)}";
            var repository = await ado.GetBoundedJsonAsync(api + "?api-version=7.1", 100000, ct);
            var repoId = repository["id"]?.GetValue<string>() ?? "";
            var projectId = repository["project"]?["id"]?.GetValue<string>() ?? "";
            if (!Guid.TryParse(repoId, out _) || !Guid.TryParse(projectId, out _))
                throw new InvalidDataException("Reference PR repository identity was not verified by ADO.");
            var fresh = await ado.GetBoundedJsonAsync(
                $"https://dev.azure.com/{org}/{projectId}/_apis/git/repositories/{repoId}/pullrequests/{id}?api-version=7.1", 100000, ct);
            var pr = new PrView
            {
                Key = $"{org}/{projectId}/{repoId}/{id}", Id = id, Title = fresh["title"]?.GetValue<string>() ?? "",
                Url = url, Org = org, Project = repository["project"]?["name"]?.GetValue<string>() ?? project,
                ProjectId = projectId, Repo = repository["name"]?.GetValue<string>() ?? repo, RepoId = repoId,
                Author = new("Reference PR", "", ""),
            };
            context = await ExplainContext.CaptureAsync(ado, pr, ct);
            if (context.Changes.Count > 20)
                throw new InvalidOperationException("Reference PR exceeds the 20-change inspection limit. Choose a smaller reference PR.");
            var diffs = new List<string>();
            foreach (var change in context.Changes)
            {
                var diff = await context.ReadChangeAsync(change.Index, ct);
                if (diff.Contains("[DIFF TRUNCATED]", StringComparison.Ordinal) ||
                    diff.StartsWith("No complete text diff", StringComparison.Ordinal) || change.Folder)
                    throw new InvalidOperationException($"Reference PR diff {change.Path} is unavailable, nontextual or exceeds the size limit. No unread reference was attached.");
                diffs.Add(diff);
                if (JsonSerializer.Serialize(diffs, Bridge.Json).Length > 32000)
                    throw new InvalidOperationException("Reference PR exceeds the 32,000-character serialized diff limit. Choose a smaller reference PR.");
            }
            await context.VerifyAsync(ct);
            var reference = new ReferencePrContext(url, context, string.Join("\n\n", diffs));
            if (JsonSerializer.Serialize(reference.Snapshot(), Bridge.Json).Length > 36000)
                throw new InvalidOperationException("Reference PR evidence exceeds the bounded context limit. Choose a smaller reference PR.");
            return reference;
        }
        catch (AdoException ex)
        {
            context?.Dispose();
            var reason = ex.Status is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized
                ? "access denied" : ex.Status == System.Net.HttpStatusCode.NotFound ? "not found or inaccessible" : "read failed";
            throw new InvalidOperationException($"Reference PR {url}: {reason}. No unread reference was attached.", ex);
        }
        catch { context?.Dispose(); throw; }
    }
}

public sealed partial class PrService
{
    internal Task<ReferencePrContext> GetReferencePrContextAsync(string url, CancellationToken ct) =>
        ReferencePrContext.CaptureAsync(_ado, url, ct);
}
