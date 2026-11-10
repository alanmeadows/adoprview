using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Windows;

namespace AdoPrView;

/// JSON-RPC bridge between the WebView2 UI and the services.
public sealed class Bridge(PrService prs, SettingsStore store, Func<string, Task> post, Action<bool> kickRefresh,
    IApprovalProvider? approval = null, IApprovalUi? approvalUi = null, Func<string, JsonObject, Task<object>>? copilotAction = null,
    Func<string, Task<object>>? popAction = null)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public Task PushSnapshotAsync(Snapshot s) => post(JsonSerializer.Serialize(new { @event = "snapshot", data = s }, Json));
    public Task PushSettingsAsync() => post(JsonSerializer.Serialize(new { @event = "settings", data = store.Current }, Json));
    public Task PushToastAsync(string kind, string text) => post(JsonSerializer.Serialize(new { @event = "toast", data = new { kind, text } }, Json));
    public Task PushCopilotRunAsync(object run) => post(JsonSerializer.Serialize(new { @event = "copilotRun", data = run }, Json));

    public async Task HandleAsync(string message)
    {
        JsonNode? req = null;
        try
        {
            req = JsonNode.Parse(message);
            var method = req?["method"]?.GetValue<string>() ?? "";
            var p = req?["params"] as JsonObject ?? new JsonObject();
            var result = await DispatchAsync(method, p);
            await post(JsonSerializer.Serialize(new { id = req?["id"]?.GetValue<int>(), result }, Json));
        }
        catch (Exception ex)
        {
            await post(JsonSerializer.Serialize(new { id = req?["id"]?.GetValue<int>(), error = ex.Message }, Json));
        }
    }

    IApprovalProvider Provider() => approval ?? throw new InvalidOperationException("No approval provider is enabled.");
    IApprovalUi Ui() => approvalUi ?? throw new InvalidOperationException("No approval provider is enabled.");

    static string Str(JsonObject p, string k) => p[k]?.GetValue<string>() ?? throw new ArgumentException($"missing '{k}'");

    async Task<object?> DispatchAsync(string method, JsonObject p)
    {
        switch (method)
        {
            case "init":
                return new { settings = store.Current, snapshot = prs.Current,
                    runs = copilotAction is null ? Array.Empty<object>() : await copilotAction("listCopilot", p),
                    version = typeof(Bridge).Assembly.GetName().Version?.ToString(3) };
            case "refresh":
                kickRefresh(true);
                return null;
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
                if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Only https URLs can be opened");
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
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
                    // Update known fields in place so fields from other versions survive.
                    var autoComplete = s.AutoComplete ??= new();
                    if (incoming.AutoComplete is { } ac)
                    {
                        autoComplete.MergeStrategy = ac.MergeStrategy;
                        autoComplete.DeleteSourceBranch = ac.DeleteSourceBranch;
                        autoComplete.TransitionWorkItems = ac.TransitionWorkItems;
                    }
                    s.TriageInstructions = string.IsNullOrWhiteSpace(incoming.TriageInstructions) ? null : incoming.TriageInstructions;
                    s.WslDistro = string.IsNullOrWhiteSpace(incoming.WslDistro) ? null : incoming.WslDistro.Trim();
                    s.WslRepoDirectories = (incoming.WslRepoDirectories ?? new()).Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
                        .ToDictionary(kv => kv.Key.ToLowerInvariant(), kv => kv.Value.Trim(), StringComparer.OrdinalIgnoreCase);
                });
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
