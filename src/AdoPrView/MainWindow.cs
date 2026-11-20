using System.IO;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AdoPrView;

public sealed class MainWindow : Window, IApprovalUi
{
    readonly WebView2 _web = new();
    readonly SettingsStore _store;
    readonly PrService _prs;
    readonly PopBrowserPane _pop;
    readonly IApprovalProvider? _approval;
    readonly CopilotRunService _copilot = new();
    readonly ExplainService _explain;
    readonly InvestigationService _investigation;
    readonly AzureSpeech _speech = new();
    readonly VideoArtifacts _videoArtifacts = new();
    readonly VideoRenderer _videoRenderer;
    readonly VideoService _video;
    CancellationTokenSource? _speechSetup;
    Task<object>? _speechSetupWork;
    readonly ConcurrentDictionary<string, byte> _copilotStarting = new(StringComparer.OrdinalIgnoreCase);
    readonly Options _opts;
    readonly AppDiagnostics _diagnostics;
    readonly System.Windows.Forms.NotifyIcon _tray;
    readonly HumanAttentionSound _attentionSound = new();
    readonly Dictionary<string, Lane> _pendingNotices = new(StringComparer.OrdinalIgnoreCase);
    Bridge? _bridge;
    CancellationTokenSource _wake = new();
    bool _uiReady;
    bool _quitting;
    bool _shutdownComplete;
    bool _shutdownInProgress;
    string _shutdownReason = "window-close";
    int _reorderRequested;

    internal MainWindow(Options opts, AppDiagnostics diagnostics)
    {
        _opts = opts;
        _diagnostics = diagnostics;
        _store = new SettingsStore();
        (_approval, var approvalError) = ApprovalProviderLoader.Load(_store.Current.ApprovalProvider);
        _prs = new PrService(new AdoClient(), _store, _approval, approvalError);
        _explain = new ExplainService(_prs.GetExplainContextAsync);
        _investigation = new InvestigationService(_prs.GetInvestigationSourceAsync, _prs.PostLensFindingsAsync);
        _videoRenderer = new VideoRenderer(new AzureSpeechVoice(_speech, () => _store.Current.VideoSpeech), warning: message =>
        {
            _diagnostics.Record("video-cleanup-warning", message);
            _ = _bridge?.PushToastAsync("error", message);
        });
        _video = new VideoService(_prs.GetVideoContextAsync, _prs.CheckVideoAvailable, _videoArtifacts, _videoRenderer);
        _pop = new PopBrowserPane(_prs.RefreshPopAsync);

        Title = "ADO PR View";
        AppTheme.ApplyWindow(this, _web, _store.Current.Theme);
        var layout = new Grid();
        var tableRow = new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 180 };
        var dividerRow = new RowDefinition { Height = new GridLength(0) };
        var popRow = new RowDefinition { Height = new GridLength(0) };
        layout.RowDefinitions.Add(tableRow);
        layout.RowDefinitions.Add(dividerRow);
        layout.RowDefinitions.Add(popRow);
        layout.Children.Add(_web);
        var divider = PopBrowserPane.CreateDivider();
        divider.Visibility = Visibility.Collapsed;
        Grid.SetRow(divider, 1);
        Grid.SetRow(_pop, 2);
        layout.Children.Add(divider);
        layout.Children.Add(_pop);
        _pop.OpenChanged += open =>
        {
            tableRow.Height = new GridLength(open ? 2 : 1, GridUnitType.Star);
            dividerRow.Height = new GridLength(open ? 10 : 0);
            popRow.Height = open ? new GridLength(3, GridUnitType.Star) : new GridLength(0);
            popRow.MinHeight = open ? 220 : 0;
            divider.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        };
        _pop.PolicyChecked += (target, result) => _ = _bridge?.PushToastAsync("info", $"{target.Repo} !{target.Id}: {result.Message}");
        _pop.Error += message => _ = _bridge?.PushToastAsync("error", message);
        _web.GotKeyboardFocus += async (_, _) => await _pop.CheckAfterReturnAsync();
        Content = layout;
        MinWidth = 900;
        MinHeight = 500;
        TaskbarItemInfo = new TaskbarItemInfo();
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/app.ico")); } catch { }
        RestorePlacement();

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Text = "ADO PR View",
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!),
            Visible = !opts.HeadlessCapture,
        };
        _tray.MouseClick += (_, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) ShowFromTray(); };
        _tray.BalloonTipClicked += (_, _) => ShowFromTray();
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowFromTray());
        menu.Items.Add("Refresh now", null, (_, _) => Kick(reorder: true));
        menu.Items.Add("Exit", null, (_, _) => { _shutdownReason = "tray-exit"; _quitting = true; Close(); });
        _tray.ContextMenuStrip = menu;

        SourceInitialized += (_, _) => ApplyTheme(_store.Current.Theme);
        Loaded += async (_, _) => await InitAsync();
        StateChanged += (_, _) => _diagnostics.Record("window-state", WindowState.ToString());
        IsVisibleChanged += (_, _) => _diagnostics.Record("window-visibility", IsVisible ? "visible" : "hidden");
        Closing += async (_, e) =>
        {
            if (_shutdownComplete) return;
            _diagnostics.Record("window-close-requested", _shutdownReason);
            SavePlacement();
            if (ShouldKeepInTray(_store.Current, _quitting, _opts.HeadlessCapture, WindowState))
            {
                // Closing the window keeps the poller alive in the tray.
                e.Cancel = true;
                Hide();
                _diagnostics.Record("close-kept-in-tray");
                _tray.ShowBalloonTip(2500, "ADO PR View is still running", "Watching your PRs from the tray. Right-click the tray icon to exit.", System.Windows.Forms.ToolTipIcon.None);
                _quitting = false;
                return;
            }
            e.Cancel = true;
            if (_shutdownInProgress) return;
            _shutdownInProgress = true;
            _diagnostics.Record("shutdown-started", _shutdownReason);
            try
            {
                _speechSetup?.Cancel();
                if (_speechSetupWork is not null)
                    try { await _speechSetupWork; } catch (Exception ex) { _diagnostics.Record("speech-setup-stopped", ex.Message); }
                await _video.DisposeAsync();
                _speechSetup?.Dispose();
                _speechSetup = null;
                _speech.Dispose();
                await _explain.DisposeAsync();
                await _investigation.DisposeAsync();
                await _copilot.DisposeAsync();
                _pop.Dispose();
                _shutdownComplete = true;
                _tray.Visible = false;
                _tray.Dispose();
                _attentionSound.Dispose();
                _diagnostics.Record("shutdown-teardown-completed", _shutdownReason);
                _ = Dispatcher.BeginInvoke(new Action(Close));
            }
            catch (Exception ex)
            {
                _shutdownInProgress = false;
                _quitting = false;
                _diagnostics.Record("shutdown-blocked", _shutdownReason, ex);
                _shutdownReason = "window-close";
                ShowFromTray();
                MessageBox.Show(this, "The app cannot exit safely: " + ex.Message, "Copilot task still needs attention", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        Closed += (_, _) =>
        {
            _diagnostics.Record("window-closed", _shutdownReason);
            Application.Current.Shutdown();
        };
    }

    void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    void Kick(bool reorder = false)
    {
        if (reorder) Interlocked.Exchange(ref _reorderRequested, 1);
        _wake.Cancel();
    }

    async Task InitAsync()
    {
        var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(SettingsStore.Dir, "WebView2"));
        await _web.EnsureCoreWebView2Async(env);
        var core = _web.CoreWebView2;
        core.ProcessFailed += (_, e) => _diagnostics.Record("webview-process-failed", e.ProcessFailedKind.ToString());
        core.Settings.AreDevToolsEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = true;
        core.Settings.IsZoomControlEnabled = true;
        core.SetVirtualHostNameToFolderMapping("app.local", Path.Combine(AppContext.BaseDirectory, "wwwroot"), CoreWebView2HostResourceAccessKind.Deny);
        VideoMedia.Attach(core, _videoArtifacts, ex => _diagnostics.Record("video-media-failed", ex.Message));
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (e.Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri) { UseShellExecute = true });
        };

        _bridge = new Bridge(_prs, _store, PostAsync, Kick, _approval, this, HandleCopilotAsync, HandlePopAsync,
            HandleExplainAsync, themeChanged: ApplyTheme, videoAction: HandleVideoAsync, investigationAction: HandleInvestigationAsync);
        _copilot.Changed += run => _ = _bridge.PushCopilotRunAsync(RunForUi(run));
        _explain.Changed += state => _ = _bridge.PushExplanationAsync(state);
        _investigation.Changed += state => _ = _bridge.PushInvestigationAsync(state);
        _video.Changed += state => _ = _bridge.PushVideoAsync(state);
        core.WebMessageReceived += (_, e) =>
        {
            if (!PopTarget.IsDashboardSource(e.Source)) return;
            var msg = e.TryGetWebMessageAsString();
            if (msg == "ready") { _uiReady = true; return; }
            _ = _bridge.HandleAsync(msg);
        };
        _prs.Changed += s => Dispatcher.InvokeAsync(() => OnSnapshot(s));
        _prs.LaneChanged += (pr, _) =>
        {
            if (!pr.IsIgnoredDraft && pr.Lane is Lane.NeedsHuman or Lane.Blocked)
                lock (_pendingNotices)
                {
                    _pendingNotices[pr.Key] = pr.Lane;
                }
        };

        core.Navigate(AppTheme.DashboardUri(_store.Current.Theme, _opts.Mock));
        if (!_opts.Mock) _ = RecoverCopilotAsync();
        if (!_opts.Mock) _ = PollLoopAsync();
        if (_opts.ScreenshotPath != null) _ = CaptureAsync();
    }

    static object RunForUi(CopilotRunState run) => new
    {
        run.Key, run.Id, run.Title, run.Repo, run.RunId, run.Status, run.Message, run.Output, run.ExitCode,
        run.WorktreePath, run.WorktreeStatus, run.WorktreeRetained, run.Distro, run.SourceDirectory, run.StartedAt, run.FinishedAt,
        run.SessionId, run.ResumeAttempts, run.LogPath, run.OutputOffset,
    };

    async Task<object> HandleExplainAsync(string action, JsonObject parameters)
    {
        if (action == "listExplanations") return _explain.States;
        var key = parameters["key"]?.GetValue<string>() ?? throw new ArgumentException("Missing PR key.");
        if (action == "cancelExplanation")
            return await _explain.CancelAsync(key, parameters["requestId"]?.GetValue<string>() ?? throw new ArgumentException("Missing explanation ID."));
        _prs.Find(key); // Reject keys that are not in the loaded dashboard before starting work.
        return _explain.Start(key);
    }

    async Task<object> HandleInvestigationAsync(string action, JsonObject p)
    {
        if (action == "listInvestigations") return _investigation.States;
        var key = p["key"]?.GetValue<string>() ?? throw new ArgumentException("Missing PR key.");
        if (action == "askPr") _prs.Find(key);
        var conversation = p["conversationId"]?.GetValue<string>() ?? "";
        var request = p["requestId"]?.GetValue<string>() ?? "";
        return action switch
        {
            "askPr" => _investigation.Ask(key, conversation, p["kind"]?.GetValue<string>() ?? "question",
                p["question"]?.GetValue<string>() ?? ""),
            "cancelInvestigation" => await _investigation.CancelAsync(key, conversation, request),
            "resetInvestigation" => await _investigation.ResetAsync(key, conversation),
            "postLensFindings" => await _investigation.PostAsync(key, conversation, request,
                (p["findingIds"] as JsonArray)?.Select(n => n!.GetValue<string>()).ToArray() ?? [],
                p["confirmed"]?.GetValue<bool>() == true),
            _ => throw new ArgumentException("Unknown investigation action."),
        };
    }

    async Task<object> HandleVideoAsync(string action, JsonObject parameters)
    {
        if (action == "listVideos") return _video.States;
        if (action == "cancelSpeechSetup") { _speechSetup?.Cancel(); return new { cancelled = true }; }
        if (action is "listSpeechSubscriptions" or "findSpeechResources" or "selectSpeechResource")
        {
            if (_speechSetupWork is { IsCompleted: false }) throw new InvalidOperationException("Speech setup is already checking Azure. Wait or cancel it first.");
            if (_video.States.Any(s => VideoService.Active(s.Status))) throw new InvalidOperationException("Finish or cancel the current video before changing its Speech connection.");
            _speechSetup?.Dispose();
            _speechSetup = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            _speechSetupWork = SpeechSetupAsync(action, parameters, _speechSetup.Token);
            try { return await _speechSetupWork; }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException("Video setup was cancelled or reached its five-minute limit. Check Azure CLI sign-in and network access, then retry setup.");
            }
        }
        var key = parameters["key"]?.GetValue<string>() ?? throw new ArgumentException("Missing PR key.");
        if (action == "cancelVideo")
            return await _video.CancelAsync(key, parameters["requestId"]?.GetValue<string>() ?? throw new ArgumentException("Missing video ID."));
        _prs.CheckVideoAvailable(key);
        if (_store.Current.VideoSpeech is null) throw new InvalidOperationException("Choose an Azure Speech resource before generating a video.");
        return _video.Start(key);
    }

    async Task<object> SpeechSetupAsync(string action, JsonObject parameters, CancellationToken ct)
    {
        var epoch = parameters["epoch"]?.GetValue<int>() ?? 0;
        if (_bridge is not null) await _bridge.PushSpeechSetupAsync(epoch,
            "Checking bundled Copilot runtime, real-cat assets and Windows H.264/AAC encoders. No FFmpeg or voice-model download is needed.");
        await _videoRenderer.PrepareDependenciesAsync(ct);
        if (_bridge is not null) await _bridge.PushSpeechSetupAsync(epoch, action == "selectSpeechResource"
            ? "Local dependencies ready. Verifying Azure Speech access with a short generic synthesis test..."
            : "Local dependencies ready. Checking Azure CLI sign-in and accessible resources...");
        if (action == "listSpeechSubscriptions") return await _speech.SubscriptionsAsync(ct);
        if (action == "findSpeechResources")
            return await _speech.FindAsync(parameters["subscription"]?.GetValue<string>() ?? "", ct);
        var selected = await _speech.SelectAsync(parameters["resourceId"]?.GetValue<string>() ?? "",
            parameters["voice"]?.GetValue<string>() ?? "", ct, testSynthesis: true);
        ct.ThrowIfCancellationRequested();
        _store.Update(s => s.VideoSpeech = selected);
        return _store.Current;
    }

    async Task RecoverCopilotAsync()
    {
        try { await _copilot.RecoverAsync(); }
        catch { if (_bridge is not null) await _bridge.PushToastAsync("error", "Saved Copilot run history could not be read. No recovery or cleanup is assumed safe."); }
    }

    async Task<object> HandlePopAsync(string key)
    {
        var target = _prs.GetPopTarget(key);
        await _pop.OpenAsync(target);
        return new { message = $"Inline PoP opened for {target.Repo} !{target.Id}. Complete the website's human key/sign-in flow, then Check status; approval is not inferred." };
    }

    async Task<object> HandleCopilotAsync(string action, JsonObject parameters)
    {
        if (!_opts.Mock) await _copilot.RecoverAsync();
        if (action == "listCopilot") return _copilot.Runs.Select(RunForUi).ToArray();
        var key = parameters["key"]?.GetValue<string>() ?? throw new ArgumentException("Missing PR key.");
        if (action == "cancelCopilot")
        {
            var runId = parameters["runId"]?.GetValue<string>() ?? throw new ArgumentException("Missing run ID.");
            var state = await _copilot.CancelAsync(key, runId) ?? throw new InvalidOperationException("The selected Copilot run no longer exists.");
            return RunForUi(state);
        }
        if (action != "startCopilot") throw new ArgumentException("Unknown Copilot action.");
        if (!_copilotStarting.TryAdd(key, 0))
            throw new InvalidOperationException("A Copilot run is already being prepared for this PR.");
        try
        {
            if (_copilot.Runs.Any(r => r.IsActive && r.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("A Copilot run is already active for this PR.");
            var source = await _prs.GetCopilotSourceAsync(key);
            var pr = source.Pr;
            var settings = _store.Current;
            settings.WslRepoDirectories.TryGetValue($"{pr.Org}/{pr.Project}/{pr.Repo}".ToLowerInvariant(), out var directory);
            var prompt = source.Prompt + $"\nVerified source: {source.SourceRef} at {source.SourceCommit}. The isolated run branch is not the PR source branch; do not assume they have the same name.\n";
            var started = await _copilot.StartAsync(new(pr.Key, pr.Id, pr.Title, pr.Org, pr.Project, pr.Repo, pr.RepoId,
                source.SourceRef, source.SourceCommit, prompt, settings.WslDistro, directory, PushToSource: true));
            return RunForUi(started);
        }
        finally { _copilotStarting.TryRemove(key, out _); }
    }

    public Task<T> OnUiThreadAsync<T>(Func<Window?, T> action) => Dispatcher.InvokeAsync(() => action(this)).Task;

    async Task PostAsync(string json)
    {
        await Dispatcher.InvokeAsync(() =>
        {
            try { _web.CoreWebView2?.PostWebMessageAsJson(json); } catch { }
        });
    }

    void OnSnapshot(Snapshot s)
    {
        _ = _bridge?.PushSnapshotAsync(s);
        var visible = s.Prs.Where(p => !p.IsHidden && !p.IsIgnoredDraft && !(_store.Current.IgnoreDrafts && p.IsDraft)).ToList();
        var needs = visible.Count(p => p.Lane == Lane.NeedsHuman);
        var blocked = visible.Count(p => p.Lane == Lane.Blocked);
        Title = needs > 0 ? $"ADO PR View — {needs} need human action" : "ADO PR View";
        _tray.Text = $"ADO PR View: {needs} need human action, {blocked} blocked";
        TaskbarItemInfo.Overlay = needs > 0 ? Badge(needs) : null;
        TaskbarItemInfo.Description = _tray.Text;

        if (s.Refreshing) return;
        var (notices, playHumanSound) = DrainNotices(visible, _pendingNotices, _store.Current, _opts.HeadlessCapture);
        if (playHumanSound)
            _attentionSound.Play();
        if (notices.Length > 0 && _store.Current.Notifications && !_opts.HeadlessCapture && !IsActive)
            _tray.ShowBalloonTip(6000, notices.Length == 1 ? "PR status changed" : $"{notices.Length} PRs changed", string.Join("\n", notices.Take(4)), System.Windows.Forms.ToolTipIcon.Info);
    }

    internal static bool ShouldKeepInTray(AppSettings settings, bool quitting, bool headlessCapture, WindowState state) =>
        !quitting && !headlessCapture && state != WindowState.Minimized && settings.Notifications;

    internal static (string[] Notices, bool PlayHumanSound) DrainNotices(IEnumerable<PrView> visible,
        Dictionary<string, Lane> pendingNotices, AppSettings settings, bool headlessCapture)
    {
        lock (pendingNotices)
        {
            var pending = visible.Where(p => !p.IsHidden && !p.IsIgnoredDraft && !(settings.IgnoreDrafts && p.IsDraft) && p.Error is null &&
                pendingNotices.TryGetValue(p.Key, out var lane) && lane == p.Lane).ToList();
            var notices = pending.Select(p => $"{(p.Lane == Lane.NeedsHuman ? "Needs Human" : "Blocked")}: !{p.Id} {p.Title}").ToArray();
            pendingNotices.Clear();
            return (notices, settings.Sound && !headlessCapture && pending.Any(p => p.Lane == Lane.NeedsHuman));
        }
    }

    static ImageSource Badge(int n)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xf5, 0x9e, 0x0b)), null, new Point(16, 16), 16, 16);
            var ft = new FormattedText(n > 9 ? "9+" : n.ToString(), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), n > 9 ? 15 : 19, Brushes.Black, 1.0);
            dc.DrawText(ft, new Point(16 - ft.Width / 2, 16 - ft.Height / 2));
        }
        var bmp = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        return bmp;
    }

    async Task PollLoopAsync()
    {
        while (true)
        {
            var wake = _wake = new CancellationTokenSource();
            await _prs.RefreshAsync(reorder: Interlocked.Exchange(ref _reorderRequested, 0) != 0);
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(30, _store.Current.RefreshSeconds)), wake.Token); }
            catch (TaskCanceledException) { }
        }
    }

    async Task CaptureAsync()
    {
        // Wait for UI + first real data, then optionally run a script, then capture.
        var deadline = DateTime.UtcNow.AddSeconds(_opts.Mock ? 15 : 180);
        while (DateTime.UtcNow < deadline && (!_uiReady || (!_opts.Mock && _prs.Current.LastRefresh is null)))
            await Task.Delay(250);
        await Task.Delay(800);
        if (_opts.Eval != null)
        {
            await _web.CoreWebView2.ExecuteScriptAsync(_opts.Eval);
            await Task.Delay(_opts.EvalWaitMs);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_opts.ScreenshotPath!))!);
        await using (var fs = File.Create(_opts.ScreenshotPath!))
            await _web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
        _quitting = true;
        _shutdownReason = "capture-completed";
        Close();
    }

    void ApplyTheme(string theme)
    {
        AppTheme.ApplyWindow(this, _web, theme);
        _pop.ApplyTheme(theme);
    }

    void RestorePlacement()
    {
        if (_opts.Width is int w && _opts.Height is int h)
        {
            Width = w; Height = h;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }
        var p = _store.Current.Window;
        if (p is null || p.Width < 400 || p.Height < 300)
        {
            Width = 1500; Height = 900;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }
        var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!vs.IntersectsWith(new Rect(p.Left, p.Top, p.Width, p.Height))) { WindowStartupLocation = WindowStartupLocation.CenterScreen; }
        else { Left = p.Left; Top = p.Top; }
        Width = p.Width; Height = p.Height;
        if (p.Maximized) WindowState = WindowState.Maximized;
    }

    void SavePlacement()
    {
        if (_opts.HeadlessCapture) return;
        var r = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (r.IsEmpty) return;
        var maximized = WindowState == WindowState.Maximized;
        _store.Update(s => StorePlacement(s, r, maximized));
    }

    // Updates the existing placement in place so fields from other versions survive.
    internal static void StorePlacement(AppSettings s, Rect r, bool maximized)
    {
        var w = s.Window ??= new();
        (w.Left, w.Top, w.Width, w.Height, w.Maximized) = (r.Left, r.Top, r.Width, r.Height, maximized);
    }
}
