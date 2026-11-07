using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AdoPrView;

public sealed class PopBrowserPane : Border, IDisposable
{
    readonly WebView2 browser = new();
    readonly Grid browserArea = new();
    readonly TextBlock title = new(), origin = new(), policy = new(), navigation = new();
    readonly Button back, reload, check, closePopup;
    readonly PopSession session;
    readonly Style buttonStyle = (Style)XamlReader.Parse("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Button">
          <Setter Property="FontFamily" Value="Segoe UI"/>
          <Setter Property="FontSize" Value="12"/>
          <Setter Property="Background" Value="#1c212b"/>
          <Setter Property="Foreground" Value="#b4bac8"/>
          <Setter Property="BorderBrush" Value="#2e3544"/>
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
              <Setter Property="Background" Value="#242a36"/>
              <Setter Property="Foreground" Value="#e7e9ef"/>
              <Setter Property="BorderBrush" Value="#7e879a"/>
            </Trigger>
            <Trigger Property="IsPressed" Value="True"><Setter Property="Background" Value="#2e3544"/></Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True"><Setter Property="BorderBrush" Value="#7c83ff"/></Trigger>
            <Trigger Property="IsEnabled" Value="False">
              <Setter Property="Foreground" Value="#7e879a"/><Setter Property="Opacity" Value="0.5"/>
            </Trigger>
          </Style.Triggers>
        </Style>
        """);
    readonly string userDataDirectory;
    CoreWebView2Environment? environment;
    Task? initialization;
    WebView2? popup;
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
    {
        session = new(refresh);
        this.userDataDirectory = userDataDirectory ?? Path.Combine(SettingsStore.Dir, "PoPWebView2");
        Visibility = Visibility.Collapsed;
        Margin = new Thickness(12, 0, 12, 12);
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x2e, 0x35, 0x44));
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(10);
        Background = new SolidColorBrush(Color.FromRgb(0x12, 0x15, 0x1b));
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
            text.Foreground = new SolidColorBrush(Color.FromRgb(0xb4, 0xba, 0xc8));
        }
        title.Foreground = new SolidColorBrush(Color.FromRgb(0xe7, 0xe9, 0xef));
        notice.Foreground = navigation.Foreground = new SolidColorBrush(Color.FromRgb(0x7e, 0x87, 0x9a));
        var headerFrame = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0x1a, 0x22)),
            CornerRadius = new CornerRadius(9, 9, 0, 0), Child = header,
        };
        DockPanel.SetDock(headerFrame, Dock.Top);
        root.Children.Add(headerFrame);
        browserArea.Children.Add(browser);
        // Keep the rectangular HWND inset; WPF rounded corners cannot clip WebView2.
        root.Children.Add(new Border
        {
            Margin = new Thickness(8, 0, 8, 8), BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3c, 0x45, 0x58)),
            Child = browserArea,
        });
        Child = root;
        session.Changed += UpdatePolicy;
        UpdatePolicy();
    }

    CoreWebView2? Active => (popup ?? browser).CoreWebView2;

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
                        Width="56" Height="3" CornerRadius="1.5" Background="#434b60"/>
              </Grid>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Grip" Property="Background" Value="#7c83ff"/></Trigger>
                <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Grip" Property="Background" Value="#7c83ff"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
            """),
    };

    public async Task OpenAsync(PopTarget target)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var navigate = !session.IsOpen || session.Selection?.Target != target;
        var captured = navigate ? session.Open(target) : session.Selection!;
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
        try
        {
            var options = new CoreWebView2EnvironmentOptions { AllowSingleSignOnUsingOSPrimaryAccount = true };
            environment = await CoreWebView2Environment.CreateAsync(null, userDataDirectory, options);
            await browser.EnsureCoreWebView2Async(environment);
            Configure(browser, allowBlank: false);
        }
        catch (Exception ex)
        {
            navigation.Text = "Embedded Edge could not start. Use Open in Edge (external), or close and retry after checking the WebView2 runtime.";
            initialization = null;
            throw new InvalidOperationException(navigation.Text + $" (0x{ex.HResult:X8})");
        }
    }

    void Configure(WebView2 view, bool allowBlank)
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
            UpdateNavigation();
            if (ReferenceEquals(core, Active))
                navigation.Text = e.IsSuccess && e.HttpStatusCode < 400
                    ? "Website loaded. Complete the human PoP flow, then Check status."
                    : $"Website navigation failed ({e.WebErrorStatus}, HTTP {e.HttpStatusCode}). Reload or use Open in Edge (external); no approval is inferred.";
        };
        core.NewWindowRequested += async (_, e) =>
        {
            e.Handled = true;
            using var deferral = e.GetDeferral();
            if (!session.IsOpen || !PopTarget.AllowsNavigation(e.Uri, allowBlank: true) || popup is not null)
            {
                navigation.Text = "The website popup was blocked. Finish/close any existing sign-in popup, or use Open in Edge (external).";
                return;
            }
            var captured = session.Selection;
            var child = popup = new WebView2();
            browserArea.Children.Add(child);
            try
            {
                await child.EnsureCoreWebView2Async(environment);
                if (!session.IsOpen || session.Selection != captured || popup != child)
                {
                    if (popup == child) ClosePopup();
                    return;
                }
                Configure(child, allowBlank: true);
                e.NewWindow = child.CoreWebView2;
                SurfaceCreated?.Invoke(child.CoreWebView2);
                closePopup.Visibility = Visibility.Visible;
                UpdateNavigation();
            }
            catch (Exception ex)
            {
                if (popup == child)
                {
                    ClosePopup();
                    navigation.Text = $"Embedded sign-in popup failed (0x{ex.HResult:X8}). Use Open in Edge (external).";
                }
            }
        };
        core.WindowCloseRequested += (_, _) => { if (view == popup) ClosePopup(); else _ = GuardTaskAsync(CloseAsync); };
        core.LaunchingExternalUriScheme += (_, e) =>
        {
            e.Cancel = true;
            navigation.Text = "The website requested an external application. It was not opened automatically; use Open in Edge (external) if required.";
        };
        core.DownloadStarting += (_, e) => { e.Cancel = true; navigation.Text = "Downloads are disabled in this PoP pane."; };
        core.ProcessFailed += (_, _) => navigation.Text = "The embedded website process failed. Reload or use Open in Edge (external).";
        view.GotKeyboardFocus += (_, _) => browserUsed = true;
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
            var child = popup;
            popup = null;
            browserArea.Children.Remove(child);
            child.Dispose();
        }
        closePopup.Visibility = Visibility.Collapsed;
        UpdateNavigation();
    }

    public async Task CloseAsync()
    {
        var captured = session.Close();
        ClosePopup();
        browser.CoreWebView2?.Stop();
        Visibility = Visibility.Collapsed;
        OpenChanged?.Invoke(false);
        browserUsed = false;
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
        var result = await session.CheckAsync(captured);
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
        catch (Exception ex) { navigation.Text = ex.Message; Error?.Invoke(ex.Message); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ClosePopup();
        browser.Dispose();
    }
}
