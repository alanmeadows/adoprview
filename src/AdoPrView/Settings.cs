using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AdoPrView;

public sealed class RepoRef
{
    public string Org { get; set; } = "";
    public string Project { get; set; } = "";
    public string Repo { get; set; } = "";
    public string? RepoId { get; set; }
    public string? ProjectId { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore] public string Key => $"{Org}/{Project}/{Repo}".ToLowerInvariant();
    [JsonIgnore] public string WebUrl => $"https://dev.azure.com/{Org}/{Uri.EscapeDataString(Project)}/_git/{Uri.EscapeDataString(Repo)}";

    public static RepoRef? Parse(string input)
    {
        input = input.Trim();
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri)) return null;
        var segs = uri.AbsolutePath.Trim('/').Split('/').Select(Uri.UnescapeDataString).ToList();
        string org;
        if (uri.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            if (segs.Count < 1) return null;
            org = segs[0]; segs.RemoveAt(0);
        }
        else if (uri.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase))
        {
            org = uri.Host.Split('.')[0];
            if (segs.Count > 0 && segs[0].Equals("DefaultCollection", StringComparison.OrdinalIgnoreCase)) segs.RemoveAt(0);
        }
        else return null;
        var gi = segs.FindIndex(s => s.Equals("_git", StringComparison.OrdinalIgnoreCase));
        if (gi < 0 || gi + 1 >= segs.Count) return null;
        var repo = segs[gi + 1];
        var project = gi > 0 ? segs[gi - 1] : repo;
        return new RepoRef { Org = org, Project = project, Repo = repo };
    }
}

public sealed class AutoCompleteOptions
{
    public string MergeStrategy { get; set; } = "squash";
    public bool DeleteSourceBranch { get; set; } = true;
    public bool TransitionWorkItems { get; set; } = true;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// Optional automated review bot whose PR threads get their own gate, applied only to PRs in its
/// Azure DevOps organization. Unconfigured means no bot gate.
public sealed class ReviewBotSettings
{
    public string Name { get; set; } = "";
    public string AuthorId { get; set; } = "";
    public string Organization { get; set; } = "";
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class ApprovalProviderSettings
{
    public bool Enabled { get; set; }
    public string? AssemblyPath { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class AppSettings
{
    public List<RepoRef> Repos { get; set; } = new();
    public List<string> Whitelist { get; set; } = new();
    public List<string> Hidden { get; set; } = new();   // "{repoId}:{prId}"
    public int RefreshSeconds { get; set; } = 120;
    public int MergeCheckMinutes { get; set; } = 10;
    public bool Notifications { get; set; } = true;
    public AutoCompleteOptions AutoComplete { get; set; } = new();
    public string? TriageInstructions { get; set; }
    public string? WslDistro { get; set; }
    public Dictionary<string, string> WslRepoDirectories { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public WindowPlacement? Window { get; set; }
    public ReviewBotSettings? ReviewBot { get; set; }
    public string? FixSkill { get; set; }
    public List<string> FixReviewerModels { get; set; } = new();
    public ApprovalProviderSettings? ApprovalProvider { get; set; }
    // Fields written by other versions or tools survive every save.
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class WindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class SettingsLoadException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class SettingsStore
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AdoPrView");
    public static readonly string DefaultFilePath = Path.Combine(Dir, "settings.json");
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    readonly object _gate = new();
    readonly string? _path;

    public AppSettings Current { get; private set; } = new();

    public SettingsStore() : this(DefaultFilePath) { }
    internal SettingsStore(string path) { _path = path; Current = Load(path); }
    internal SettingsStore(AppSettings settings) { Current = settings; }

    // A missing file starts empty. An unreadable or malformed file is never replaced with defaults.
    internal static AppSettings Load(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return new AppSettings(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { throw new SettingsLoadException($"Settings could not be read from {path}: {ex.Message}. The file was not changed.", ex); }
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(text, Json)
                ?? throw new SettingsLoadException($"Settings at {path} are empty (null). The file was not changed; fix or move it, then restart.");
        }
        catch (JsonException ex)
        { throw new SettingsLoadException($"Settings at {path} are not valid: {ex.Message}. The file was not changed; fix or move it, then restart.", ex); }
    }

    public void Update(Action<AppSettings> mutate)
    {
        lock (_gate)
        {
            mutate(Current);
            Save();
        }
    }

    void Save()
    {
        if (_path is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Json));
        File.Move(tmp, _path, true);
    }
}