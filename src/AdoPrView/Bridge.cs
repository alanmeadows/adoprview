using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Windows;

namespace AdoPrView;

/// JSON-RPC bridge between the WebView2 UI and the services.
public sealed class Bridge(PrService prs, SettingsStore store, Func<string, Task> post, Action<bool> kickRefresh,
    IApprovalProvider? approval = null, IApprovalUi? approvalUi = null, Func<string, JsonObject, Task<object>>? copilotAction = null,
    Func<string, Task<object>>? popAction = null, Func<string, JsonObject, Task<object>>? explainAction = null,
    Action<string>? themeChanged = null, Func<string, JsonObject, Task<object>>? videoAction = null,
    Func<string, JsonObject, Task<object>>? investigationAction = null, Action<string>? openBrowser = null,
    Action<long>? copilotAcknowledged = null)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public Task PushSnapshotAsync(Snapshot s) => post(JsonSerializer.Serialize(new { @event = "snapshot", data = s }, Json));
    public Task PushSettingsAsync() => post(JsonSerializer.Serialize(new { @event = "settings", data = store.Current }, Json));
    public Task PushToastAsync(string kind, string text) => post(JsonSerializer.Serialize(new { @event = "toast", data = new { kind, text } }, Json));
    public Task PushCopilotRunAsync(object run, long? deliveryId = null) => post(JsonSerializer.Serialize(new { @event = "copilotRun", data = run, deliveryId }, Json));
    public Task PushExplanationAsync(object state) => post(JsonSerializer.Serialize(new { @event = "explanation", data = state }, Json));
    public Task PushInvestigationAsync(object state) => post(JsonSerializer.Serialize(new { @event = "investigation", data = state }, Json));
    public Task PushVideoAsync(object state) => post(JsonSerializer.Serialize(new { @event = "prVideo", data = state }, Json));
    public Task PushSpeechSetupAsync(int epoch, string message) => post(JsonSerializer.Serialize(new
        { @event = "speechSetupProgress", data = new { epoch, message } }, Json));

    public async Task HandleAsync(string message)
    {
        int? id = null;
        string response;
        try
        {
            var req = JsonNode.Parse(message) as JsonObject ?? throw new ArgumentException("Expected a JSON request object.");
            if (req["id"] is not JsonValue idValue || !idValue.TryGetValue<int>(out var requestId))
                throw new ArgumentException("Expected an integer request ID.");
            id = requestId;
            var method = req?["method"]?.GetValue<string>() ?? "";
            var p = req?["params"] as JsonObject ?? new JsonObject();
            var result = await DispatchAsync(method, p);
            response = JsonSerializer.Serialize(new { id, result }, Json);
        }
        catch (Exception ex)
        {
            response = JsonSerializer.Serialize(new { id, error = ex.Message }, Json);
        }
        // A delivery failure is not a second RPC failure; the native owner reports it out of band.
        await post(response);
    }

    IApprovalProvider Provider() => approval ?? throw new InvalidOperationException("No approval provider is enabled.");
    IApprovalUi Ui() => approvalUi ?? throw new InvalidOperationException("No approval provider is enabled.");

    static string Str(JsonObject p, string k) => p[k]?.GetValue<string>() ?? throw new ArgumentException($"missing '{k}'");

    async Task<object?> DispatchAsync(string method, JsonObject p)
    {
        switch (method)
        {
            case "init":
                return new {
                    runs = copilotAction is null ? Array.Empty<object>() : await copilotAction("listCopilot", p),
                    explanations = explainAction is null ? Array.Empty<object>() : await explainAction("listExplanations", p),
                    investigations = investigationAction is null ? Array.Empty<object>() : await investigationAction("listInvestigations", p),
                    videos = videoAction is null ? Array.Empty<object>() : await videoAction("listVideos", p),
                    settings = store.Current, snapshot = prs.Current,
                    version = typeof(Bridge).Assembly.GetName().Version?.ToString(3) };
            case "refresh":
                kickRefresh(true);
                return null;
            case "setTheme":
                var theme = Str(p, "theme");
                store.SetTheme(theme);
                themeChanged?.Invoke(theme);
                return store.Current;
            case "refreshOne":
                await prs.RefreshOneAsync(Str(p, "key"));
                return null;
            case "openPop":
                if (popAction is null) throw new InvalidOperationException("The native inline PoP pane is unavailable.");
                return await popAction(Str(p, "key"));
            case "autoComplete":
                return await prs.SetAutoCompleteAsync(Str(p, "key"), p["enable"]?.GetValue<bool>() ?? true);
            case "publish":
                await prs.PublishAsync(Str(p, "key"));
                return null;
            case "generateApproval":
                return await prs.RequestApprovalAsync(Str(p, "key"), Ui());
            case "approve":
                var expectedVote = p["expected"]?.Deserialize<ReviewVoteTarget>(Json)
                    ?? throw new ArgumentException("Missing captured review revision. Refresh before approving.");
                return await prs.ApproveAsync(Str(p, "key"), expectedVote);
            case "approvalSettings":
                return Provider().GetSettings();
            case "approvalSettingsAction":
                var actionId = Str(p, "id");
                var provider = Provider();
                if (provider.GetSettings()?.Actions.Any(a => a.Id == actionId) != true) throw new ArgumentException("Unknown approval settings action.");
                return new { message = await provider.InvokeSettingsActionAsync(actionId, Ui()), settings = provider.GetSettings() };
            case "restartMerge":
                return await prs.RestartMergeAsync(Str(p, "key"));
            case "requeue":
                await prs.RequeueAsync(Str(p, "key"), Str(p, "evaluationId"));
                return null;
            case "logTail":
                return await prs.GetLogTailAsync(Str(p, "key"), p["buildId"]!.GetValue<int>(), p["logId"]!.GetValue<int>(), p["lines"]?.GetValue<int>() ?? 200);
            case "openUrl":
                var url = Str(p, "url");
                if (openBrowser is not null) openBrowser(url);
                else NativeActions.OpenBrowser(url);
                return null;
            case "copy":
                await SetClipboardAsync(Str(p, "text"));
                return null;
            case "triage":
            case "copyPrompt":
                var prompt = await prs.BuildTriagePromptAsync(Str(p, "key"));
                await SetClipboardAsync(prompt);
                return new { prompt };
            case "startCopilot":
            case "cancelCopilot":
                if (copilotAction is null) throw new InvalidOperationException("The native inline Copilot runner is unavailable.");
                return await copilotAction(method, p);
            case "copilotRunAck":
                copilotAcknowledged?.Invoke(p["deliveryId"]!.GetValue<long>());
                return null;
            case "explainPr":
            case "cancelExplanation":
                if (explainAction is null) throw new InvalidOperationException("The native PR explanation service is unavailable.");
                return await explainAction(method, p);
            case "askPr":
            case "cancelInvestigation":
            case "resetInvestigation":
            case "postLensFindings":
                if (investigationAction is null) throw new InvalidOperationException("The native PR investigation service is unavailable.");
                return await investigationAction(method, p);
            case "tiktokPr":
            case "cancelVideo":
            case "listSpeechSubscriptions":
            case "findSpeechResources":
            case "selectSpeechResource":
            case "cancelSpeechSetup":
                if (videoAction is null) throw new InvalidOperationException("The native PR video service is unavailable.");
                return await videoAction(method, p);
            case "hide":
            case "unhide":
                var key = Str(p, "key");
                store.Update(s =>
                {
                    s.Hidden.RemoveAll(h => h.Equals(key, StringComparison.OrdinalIgnoreCase));
                    if (method == "hide") s.Hidden.Add(key);
                });
                kickRefresh(false);
                return store.Current;
            case "addRepo":
                var repo = RepoRef.Parse(Str(p, "url")) ?? throw new ArgumentException("Not an Azure DevOps repository URL (expected …/{project}/_git/{repo})");
                if (store.Current.Repos.Any(r => r.Key == repo.Key)) throw new ArgumentException($"{repo.Repo} is already added");
                store.Update(s => s.Repos.Add(repo));
                kickRefresh(false);
                return store.Current;
            case "addRepos":
                var reposToAdd = p["repos"]!.Deserialize<List<RepoRef>>(Json)!;
                store.Update(s =>
                {
                    foreach (var r in reposToAdd)
                        if (!s.Repos.Any(x => x.Key == r.Key)) s.Repos.Add(r);
                });
                kickRefresh(false);
                return store.Current;
            case "removeRepo":
                var rk = Str(p, "key");
                store.Update(s => s.Repos.RemoveAll(r => r.Key == rk));
                kickRefresh(false);
                return store.Current;
            case "discoverRepos":
                return await prs.DiscoverReposAsync();
            case "saveSettings":
                var incoming = p["settings"]!.Deserialize<AppSettings>(Json)!;
                store.Update(s =>
                {
                    s.Whitelist = incoming.Whitelist.Select(w => w.Trim()).Where(w => w.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    s.RefreshSeconds = Math.Clamp(incoming.RefreshSeconds, 30, 3600);
                    s.MergeCheckMinutes = Math.Clamp(incoming.MergeCheckMinutes, 1, 240);
                    s.Notifications = incoming.Notifications;
                    s.IgnoreDrafts = incoming.IgnoreDrafts;
                    if (p["settings"]!.AsObject().Any(field => field.Key.Equals("sound", StringComparison.OrdinalIgnoreCase)))
                        s.Sound = incoming.Sound;
                    // Update known fields in place so fields from other versions survive.
                    var autoComplete = s.AutoComplete ??= new();
                    if (incoming.AutoComplete is { } ac)
                    {
                        autoComplete.MergeStrategy = ac.MergeStrategy;
                        autoComplete.DeleteSourceBranch = ac.DeleteSourceBranch;
                        autoComplete.TransitionWorkItems = ac.TransitionWorkItems;
                    }
                    s.TriageInstructions = string.IsNullOrWhiteSpace(incoming.TriageInstructions) ? null : incoming.TriageInstructions;
                    if (p["settings"]!.AsObject().Any(field => field.Key.Equals("additionalTriageInstructions", StringComparison.OrdinalIgnoreCase)))
                        s.AdditionalTriageInstructions = string.IsNullOrWhiteSpace(incoming.AdditionalTriageInstructions) ? null : incoming.AdditionalTriageInstructions;
                    s.WslDistro = string.IsNullOrWhiteSpace(incoming.WslDistro) ? null : incoming.WslDistro.Trim();
                    s.WslRepoDirectories = (incoming.WslRepoDirectories ?? new()).Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
                        .ToDictionary(kv => kv.Key.ToLowerInvariant(), kv => kv.Value.Trim(), StringComparer.OrdinalIgnoreCase);
                });
                prs.ApplyMonitoringSettings();
                kickRefresh(false);
                return store.Current;
            default:
                throw new ArgumentException("Unknown method " + method);
        }
    }

    static async Task SetClipboardAsync(string text)
    {
        for (var i = 0; ; i++)
        {
            try
            {
                await Application.Current.Dispatcher.InvokeAsync(() => Clipboard.SetDataObject(text, true));
                return;
            }
            catch (COMException) when (i < 5) { await Task.Delay(60); }
        }
    }

}
