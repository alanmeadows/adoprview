using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Forms.Integration;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using WebView2 = Microsoft.Web.WebView2.WinForms.WebView2;

namespace AdoPrView;

public sealed class PopBrowserPane : Border, IDisposable
{
    readonly WebView2 browser = new();
    readonly WindowsFormsHost browserHost;
    readonly Grid browserArea = new();
    readonly TextBlock title = new(), origin = new(), policy = new(), navigation = new();
    readonly Button back, reload, check, closePopup;
    readonly PopSession session;
    readonly Style buttonStyle = (Style)XamlReader.Parse("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Button">
          <Setter Property="FontFamily" Value="Segoe UI"/>
          <Setter Property="FontSize" Value="12"/>
          <Setter Property="Background" Value="{DynamicResource Theme.Control}"/>
          <Setter Property="Foreground" Value="{DynamicResource Theme.Text}"/>
          <Setter Property="BorderBrush" Value="{DynamicResource Theme.Border}"/>
          <Setter Property="BorderThickness" Value="1"/>
          <Setter Property="Padding" Value="10,5"/>
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="Button">
                <Border CornerRadius="5" Background="{TemplateBinding Background}"
                        BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}"
                        Padding="{TemplateBinding Padding}">
                  <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" RecognizesAccessKey="True"/>
                </Border>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
          <Style.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
              <Setter Property="Background" Value="{DynamicResource Theme.Hover}"/>
              <Setter Property="BorderBrush" Value="{DynamicResource Theme.Border}"/>
            </Trigger>
            <Trigger Property="IsPressed" Value="True"><Setter Property="Background" Value="{DynamicResource Theme.Active}"/></Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True"><Setter Property="BorderBrush" Value="{DynamicResource Theme.Focus}"/></Trigger>
            <Trigger Property="IsEnabled" Value="False">
              <Setter Property="Foreground" Value="{DynamicResource Theme.Muted}"/><Setter Property="Opacity" Value="0.7"/>
            </Trigger>
          </Style.Triggers>
        </Style>
        """);
    readonly string userDataDirectory;
    readonly AppDiagnostics? diagnostics;
    readonly string paneId = Guid.NewGuid().ToString("N");
    CoreWebView2Environment? environment;
    Task? initialization;
    WebView2? popup;
    WindowsFormsHost? popupHost;
    int nextSurface = 1, popupSurface;
    bool browserUsed, disposed;

    public event Action<bool>? OpenChanged;
    public event Action<PopTarget, PopPolicyResult>? PolicyChecked;
    public event Action<string>? Error;
    internal WebView2 Browser => browser;
    internal WebView2? Popup => popup;
    internal PopSession Session => session;
    internal string CurrentOrigin => origin.Text;
    internal string NavigationStatus => navigation.Text;
    internal event Action<CoreWebView2>? SurfaceCreated;

    public PopBrowserPane(Func<PopTarget, Task<PopPolicyResult>> refresh, string? userDataDirectory = null)
        : this(refresh, userDataDirectory, null) { }

    internal PopBrowserPane(Func<PopTarget, Task<PopPolicyResult>> refresh, string? userDataDirectory, AppDiagnostics? diagnostics)
    {
        session = new(refresh);
        this.diagnostics = diagnostics;
        ApplyTheme("light");
        this.userDataDirectory = userDataDirectory ?? Path.Combine(SettingsStore.Dir, "PoPWebView2");
        Visibility = Visibility.Collapsed;
        Margin = new Thickness(12, 0, 12, 12);
        SetResourceReference(BorderBrushProperty, "Theme.Border");
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(10);
        SetResourceReference(BackgroundProperty, "Theme.Subtle");
        var root = new DockPanel();
        var header = new StackPanel { Margin = new Thickness(12, 8, 12, 8) };
        var heading = new DockPanel();
        var close = Button("Close pane", () => GuardAsync(CloseAsync));
        DockPanel.SetDock(close, Dock.Right);
        heading.Children.Add(close);
        title.FontWeight = FontWeights.SemiBold;
        title.FontSize = 14;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.VerticalAlignment = VerticalAlignment.Center;
        heading.Children.Add(title);
        header.Children.Add(heading);
        var controls = new WrapPanel { Margin = new Thickness(0, 3, 0, 1) };
        back = Button("Back", () => { if (Active?.CanGoBack == true) Active.GoBack(); });
        reload = Button("Reload", () => Active?.Reload());
        controls.Children.Add(back);
        controls.Children.Add(reload);
        controls.Children.Add(Button("PR page", () => { ClosePopup(); if (session.Selection is { } s) browser.CoreWebView2?.Navigate(s.Target.Url); }));
        check = Button("Check status", () => GuardAsync(() => CheckAsync()));
        controls.Children.Add(check);
        controls.Children.Add(Button("Open in Edge (external)", () => GuardAsync(OpenExternalAsync)));
        closePopup = Button("Close sign-in popup", ClosePopup);
        closePopup.Visibility = Visibility.Collapsed;
        controls.Children.Add(closePopup);
        header.Children.Add(controls);
        origin.FontSize = 11;
        origin.Margin = new Thickness(0, 2, 0, 0);
        origin.TextTrimming = TextTrimming.CharacterEllipsis;
        header.Children.Add(origin);
        var notice = new TextBlock
        {
            Text = "Complete website sign-in and security-key/Windows Hello prompts yourself. This app-owned Edge session may be rejected by tenant policy. Opening or closing it is not approval.",
            TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 3, 0, 3),
        };
        header.Children.Add(notice);
        policy.TextWrapping = TextWrapping.Wrap;
        policy.FontSize = 12;
        header.Children.Add(policy);
        navigation.FontSize = 11;
        navigation.TextWrapping = TextWrapping.Wrap;
        header.Children.Add(navigation);
        foreach (var text in new[] { title, origin, notice, policy, navigation })
        {
            text.FontFamily = new FontFamily("Segoe UI");
            text.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Text");
        }
        notice.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Muted");
        navigation.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Muted");
        var headerFrame = new Border
        {
            CornerRadius = new CornerRadius(9, 9, 0, 0), Child = header,
        };
        headerFrame.SetResourceReference(BackgroundProperty, "Theme.Surface");
        DockPanel.SetDock(headerFrame, Dock.Top);
        root.Children.Add(headerFrame);
        // WPF WebView2 automatically closes its containing Window on website window.close().
        // A WinForms control has no such owner-close handler; keep these remote surfaces inline.
        browserHost = new WindowsFormsHost { Child = browser };
        browserArea.Children.Add(browserHost);
        // Keep the rectangular HWND inset; WPF rounded corners cannot clip WebView2.
        var browserFrame = new Border
        {
            Margin = new Thickness(8, 0, 8, 8), BorderThickness = new Thickness(1),
            Child = browserArea,
        };
        browserFrame.SetResourceReference(BorderBrushProperty, "Theme.Border");
        root.Children.Add(browserFrame);
        Child = root;
        session.Changed += UpdatePolicy;
        UpdatePolicy();
    }

    CoreWebView2? Active => (popup ?? browser).CoreWebView2;

    // Only opaque identities, counters and host-owned lifecycle/error labels belong in this trace.
    void Record(string kind, int surface = 1, string? metadata = null, Exception? exception = null, long? revision = null) =>
        diagnostics?.Record(kind, $"pane-{paneId};surface-{surface};selection-{revision ?? session.Selection?.Revision ?? 0}" +
            (metadata is null ? "" : ";" + metadata), exception);

    internal void ApplyTheme(string theme) => AppTheme.ApplyResources(this, theme);

    Button Button(string label, Action click)
    {
        var button = new Button
        {
            Content = label, ToolTip = label, Margin = new Thickness(0, 2, 6, 2), Style = buttonStyle,
        };
        button.Click += (_, _) => click();
        return button;
    }

    internal static GridSplitter CreateDivider() => new()
    {
        Height = 10, HorizontalAlignment = HorizontalAlignment.Stretch, Focusable = true,
        ResizeDirection = GridResizeDirection.Rows, ResizeBehavior = GridResizeBehavior.PreviousAndNext,
        KeyboardIncrement = 12, Cursor = Cursors.SizeNS, Margin = new Thickness(12, 0, 12, 0),
        ToolTip = "Drag, or focus and use Up/Down, to resize the PoP pane",
        Template = (ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="GridSplitter">
              <Grid Background="Transparent">
                <Border x:Name="Grip" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                        Width="56" Height="3" CornerRadius="1.5" Background="{DynamicResource Theme.Border}"/>
              </Grid>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Grip" Property="Background" Value="{DynamicResource Theme.Focus}"/></Trigger>
                <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Grip" Property="Background" Value="{DynamicResource Theme.Focus}"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
            """),
    };

    public async Task OpenAsync(PopTarget target)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var navigate = !session.IsOpen || session.Selection?.Target != target;
        var captured = navigate ? session.Open(target) : session.Selection!;
        Record("pop-pane-open", metadata: navigate ? "navigate" : "reuse");
        title.Text = "Proof of Presence - " + target.Label;
        title.ToolTip = title.Text;
        Visibility = Visibility.Visible;
        OpenChanged?.Invoke(true);
        navigation.Text = "Starting embedded Edge...";
        await EnsureInitializedAsync();
        if (!session.IsOpen || session.Selection?.Revision != captured.Revision) return;
        if (navigate) { ClosePopup(); browser.CoreWebView2.Navigate(target.Url); }
        else navigation.Text = "Continue the website flow; Check status reads the real ADO result.";
    }

    internal Task EnsureInitializedAsync() => initialization ??= InitializeAsync();

    async Task InitializeAsync()
    {
        Record("pop-initialization-started");
        try
        {
            var options = new CoreWebView2EnvironmentOptions { AllowSingleSignOnUsingOSPrimaryAccount = true };
            environment = await CoreWebView2Environment.CreateAsync(null, userDataDirectory, options);
            await browser.EnsureCoreWebView2Async(environment);
            Configure(browser, allowBlank: false, surface: 1);
            Record("pop-initialization-completed");
        }
        catch (Exception ex)
        {
            Record("pop-initialization-failed", exception: ex);
            navigation.Text = "Embedded Edge could not start. Use Open in Edge (external), or close and retry after checking the WebView2 runtime.";
            initialization = null;
            throw new InvalidOperationException(navigation.Text + $" (0x{ex.HResult:X8})", ex);
        }
    }

    void Configure(WebView2 view, bool allowBlank, int surface)
    {
        var core = view.CoreWebView2;
        // This surface never receives the dashboard's RPC, host objects or virtual-host mapping.
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.NavigationStarting += (_, e) =>
        {
            if (!PopTarget.AllowsNavigation(e.Uri, allowBlank))
            {
                e.Cancel = true;
                navigation.Text = "Blocked non-HTTPS/local navigation. Use the explicit external Edge fallback if the tenant flow requires another browser.";
            }
            else if (ReferenceEquals(view.CoreWebView2, Active))
            {
                origin.Text = "Current origin: " + PopTarget.Origin(e.Uri);
                navigation.Text = "Loading website. This does not confirm PoP.";
            }
        };
        core.FrameNavigationStarting += (_, e) => { if (!PopTarget.AllowsNavigation(e.Uri, allowBlank: true)) e.Cancel = true; };
        core.SourceChanged += (_, _) => UpdateNavigation();
        core.HistoryChanged += (_, _) => UpdateNavigation();
        core.NavigationCompleted += (_, e) =>
        {
            Record("pop-navigation-completed", surface,
                $"navigation-{e.NavigationId};success-{e.IsSuccess};http-{e.HttpStatusCode};error-{e.WebErrorStatus}");
            UpdateNavigation();
            if (ReferenceEquals(core, Active))
                navigation.Text = e.IsSuccess && e.HttpStatusCode < 400
                    ? "Website loaded. Complete the human PoP flow, then Check status."
                    : $"Website navigation failed ({e.WebErrorStatus}, HTTP {e.HttpStatusCode}). Reload or use Open in Edge (external); no approval is inferred.";
        };
        core.NewWindowRequested += async (_, e) =>
        {
            Record("pop-popup-requested", surface);
            e.Handled = true;
            using var deferral = e.GetDeferral();
            if (!session.IsOpen || !PopTarget.AllowsNavigation(e.Uri, allowBlank: true) || popup is not null)
            {
                Record("pop-popup-blocked", surface, !session.IsOpen ? "pane-closed" :
                    popup is not null ? "popup-exists" : "navigation-denied");
                navigation.Text = "The website popup was blocked. Finish/close any existing sign-in popup, or use Open in Edge (external).";
                return;
            }
            var captured = session.Selection;
            var child = popup = new WebView2();
            var childSurface = popupSurface = ++nextSurface;
            Record("pop-popup-initialization-started", childSurface);
            popupHost = new WindowsFormsHost { Child = child };
            browserArea.Children.Add(popupHost);
            try
            {
                await child.EnsureCoreWebView2Async(environment);
                if (!session.IsOpen || session.Selection != captured || popup != child)
                {
                    Record("pop-popup-initialization-abandoned", childSurface, revision: captured?.Revision);
                    if (popup == child) ClosePopup();
                    return;
                }
                Configure(child, allowBlank: true, surface: childSurface);
                e.NewWindow = child.CoreWebView2;
                Record("pop-popup-initialization-completed", childSurface);
                SurfaceCreated?.Invoke(child.CoreWebView2);
                closePopup.Visibility = Visibility.Visible;
                UpdateNavigation();
            }
            catch (Exception ex)
            {
                Record("pop-popup-initialization-failed", childSurface, exception: ex, revision: captured?.Revision);
                if (popup == child)
                {
                    ClosePopup();
                    navigation.Text = $"Embedded sign-in popup failed (0x{ex.HResult:X8}). Use Open in Edge (external).";
                }
            }
        };
        core.WindowCloseRequested += (_, _) =>
        {
            var captured = session.Selection;
            Record("pop-website-close-requested", surface, revision: captured?.Revision);
            // Dispose only after the WebView callback returns, and never close a newer surface/selection.
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                if (disposed || session.Selection != captured)
                {
                    Record("pop-website-close-ignored", surface, disposed ? "pane-disposed" : "selection-changed", revision: captured?.Revision);
                    return;
                }
                if (view == popup)
                {
                    Record("pop-website-close-applied", surface, "popup");
                    ClosePopup();
                }
                else if (view == browser && session.IsOpen)
                {
                    Record("pop-website-close-applied", surface, "pane");
                    _ = GuardTaskAsync(CloseAsync);
                }
                else Record("pop-website-close-ignored", surface, "surface-inactive", revision: captured?.Revision);
            }));
        };
        core.LaunchingExternalUriScheme += (_, e) =>
        {
            e.Cancel = true;
            navigation.Text = "The website requested an external application. It was not opened automatically; use Open in Edge (external) if required.";
        };
        core.DownloadStarting += (_, e) => { e.Cancel = true; navigation.Text = "Downloads are disabled in this PoP pane."; };
        core.ProcessFailed += (_, e) =>
        {
            Record("pop-process-failed", surface, $"kind-{e.ProcessFailedKind};reason-{e.Reason};exit-{e.ExitCode}");
            navigation.Text = "The embedded website process failed. Reload or use Open in Edge (external).";
        };
        view.Enter += (_, _) => browserUsed = true;
        if (!allowBlank) SurfaceCreated?.Invoke(core);
        UpdateNavigation();
    }

    void UpdatePolicy()
    {
        policy.Text = session.Status;
        check.IsEnabled = session.Selection is not null && !session.IsChecking;
    }

    void UpdateNavigation()
    {
        origin.Text = "Current origin: " + PopTarget.Origin(Active?.Source);
        back.IsEnabled = Active?.CanGoBack == true;
        reload.IsEnabled = Active is not null;
    }

    internal void ClosePopup()
    {
        if (popup is not null)
        {
            Record("pop-popup-dispose-started", popupSurface);
            popup = null;
            browserArea.Children.Remove(popupHost!);
            popupHost!.Dispose();
            popupHost = null;
            Record("pop-popup-dispose-completed", popupSurface);
        }
        closePopup.Visibility = Visibility.Collapsed;
        UpdateNavigation();
    }

    public async Task CloseAsync()
    {
        Record("pop-pane-close-started");
        var captured = session.Close();
        ClosePopup();
        browser.CoreWebView2?.Stop();
        Visibility = Visibility.Collapsed;
        OpenChanged?.Invoke(false);
        browserUsed = false;
        Record("pop-pane-closed");
        if (captured is not null && !session.IsChecking) await CheckAsync(captured);
    }

    public Task CheckAfterReturnAsync()
    {
        if (!browserUsed || !session.IsOpen || session.IsChecking) return Task.CompletedTask;
        browserUsed = false;
        return GuardTaskAsync(() => CheckAsync());
    }

    internal async Task CheckAsync(PopSelection? selection = null)
    {
        var captured = selection ?? session.Selection ?? throw new InvalidOperationException("Open a PR's PoP pane first.");
        Record("pop-policy-check-started", revision: captured.Revision);
        PopPolicyResult result;
        try { result = await session.CheckAsync(captured); }
        catch (Exception ex)
        {
            Record("pop-policy-check-failed", exception: ex, revision: captured.Revision);
            throw;
        }
        Record("pop-policy-check-completed", revision: captured.Revision);
        PolicyChecked?.Invoke(captured.Target, result);
    }

    internal Task OpenExternalAsync()
    {
        var target = session.Selection?.Target ?? throw new InvalidOperationException("Open a PR's PoP pane first.");
        // Edge is an explicit fallback, not the user's generic default browser.
        Process.Start(new ProcessStartInfo("msedge.exe") { UseShellExecute = true, ArgumentList = { target.Url } });
        navigation.Text = $"Opened {target.Repo} !{target.Id} in external Edge. Return here and Check status afterward.";
        return Task.CompletedTask;
    }

    async void GuardAsync(Func<Task> action) => await GuardTaskAsync(action);
    async Task GuardTaskAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex)
        {
            Record("pop-action-failed", exception: ex);
            navigation.Text = ex.Message;
            Error?.Invoke(ex.Message);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        Record("pop-pane-dispose-started");
        disposed = true;
        ClosePopup();
        browserHost.Dispose();
        Record("pop-pane-dispose-completed");
    }
}
