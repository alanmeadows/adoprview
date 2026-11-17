using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AdoPrView;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

static class ThemeTests
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("theme native chrome matches the bundled semantic palettes", () =>
        {
            var css = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "wwwroot", "themes.css"));
            foreach (var theme in new[] { "github", "light", "dark" })
            {
                var block = Regex.Match(css, $$""":root\[data-theme="{{theme}}"\]\s*\{([^}]+)""").Groups[1].Value;
                var p = AppTheme.Palette(theme);
                foreach (var (token, color) in new Dictionary<string, string>
                {
                    ["bg"] = p.Canvas, ["bg2"] = p.Subtle, ["surface"] = p.Surface, ["control"] = p.Control,
                    ["control-hover"] = p.Hover, ["control-active"] = p.Active, ["text"] = p.Text,
                    ["muted"] = p.Muted, ["control-border"] = p.Border, ["focus-ring"] = p.Focus,
                })
                    Check(Regex.IsMatch(block, $@"--{token}:\s*{Regex.Escape(color)};"), $"{theme} native {token} does not match the dashboard");
            }
            Check(AppTheme.Effective(null) == "github" && AppTheme.Effective("future-theme") == "github", "Wrong effective fallback");
            return Task.CompletedTask;
        });
        if (Environment.GetEnvironmentVariable("ADOPRVIEW_TEST_NATIVE_THEME") == "1")
            await test("theme real isolated WebView2 first paint, native bridge save, reload and PoP chrome", NativeBrowser);
    }

    static Task NativeBrowser()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "ado-theme-native-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var store = new SettingsStore(Path.Combine(root, "settings.json"));
            using var http = new HttpClient();
            var prs = new PrService(new AdoClient(http, _ => Task.FromResult(new AuthenticationHeaderValue("Bearer", "offline"))), store);
            var web = new WebView2();
            var pop = new PopBrowserPane(_ => throw new Exception("Theme tests must not invoke ADO"), Path.Combine(root, "unused-pop-profile"));
            var panel = new DockPanel();
            panel.Children.Add(web);
            var window = new Window { Width = 1200, Height = 800, Title = "Offline theme fixture", Content = panel };
            window.Loaded += async (_, _) =>
            {
                Exception? failure = null;
                CoreWebView2Environment? environment = null;
                try
                {
                    environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(root, "WebView2"));
                    await web.EnsureCoreWebView2Async(environment);
                    var core = web.CoreWebView2;
                    core.SetVirtualHostNameToFolderMapping("app.local", Path.Combine(AppContext.BaseDirectory, "wwwroot"), CoreWebView2HostResourceAccessKind.Deny);
                    // Fail closed on any unexpected navigation; never visit ADO/sign-in/model endpoints.
                    core.NavigationStarting += (_, e) => { if (!PopTarget.IsDashboardSource(e.Uri)) e.Cancel = true; };
                    core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                    core.WebResourceRequested += (_, e) =>
                    {
                        if (!PopTarget.IsDashboardSource(e.Request.Uri))
                            e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Offline only", "");
                    };
                    TaskCompletionSource<string>? init = null;
                    TaskCompletionSource? ready = null;
                    TaskCompletionSource? settled = null;
                    var bridge = new Bridge(prs, store, text => { core.PostWebMessageAsJson(text); return Task.CompletedTask; }, _ => { },
                        themeChanged: theme => { AppTheme.ApplyWindow(window, web, theme); pop.ApplyTheme(theme); });
                    core.WebMessageReceived += (_, e) =>
                    {
                        if (!PopTarget.IsDashboardSource(e.Source)) return;
                        var message = e.TryGetWebMessageAsString();
                        if (message == "ready") { ready?.TrySetResult(); return; }
                        if (message == "theme-test-settled") { settled?.TrySetResult(); return; }
                        using var request = JsonDocument.Parse(message);
                        if (request.RootElement.GetProperty("method").GetString() == "init" && init is not null) init.TrySetResult(message);
                        else _ = bridge.HandleAsync(message);
                    };
                    async Task<JsonElement> Js(string source)
                    {
                        // ExecuteScriptAsync does not await JS promises. Observe the already-rendered document.
                        var json = await core.ExecuteScriptAsync(source);
                        return JsonSerializer.Deserialize<JsonElement>(json);
                    }
                    async Task Navigate(string theme)
                    {
                        init = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        AppTheme.ApplyWindow(window, web, store.Current.Theme);
                        pop.ApplyTheme(store.Current.Theme);
                        core.Navigate(AppTheme.DashboardUri(store.Current.Theme, mock: false));
                        var request = await init.Task.WaitAsync(TimeSpan.FromSeconds(15));
                        var early = await Js("({theme:document.documentElement.dataset.theme,bg:getComputedStyle(document.body).backgroundColor,ready:!!window.__ready})");
                        Check(early.GetProperty("theme").GetString() == theme && !early.GetProperty("ready").GetBoolean(), "Saved palette waits for native init");
                        var canvas = AppTheme.Color(AppTheme.Palette(theme).Canvas);
                        Check(early.GetProperty("bg").GetString() == $"rgb({canvas.R}, {canvas.G}, {canvas.B})", "First-paint canvas is wrong");
                        await bridge.HandleAsync(request);
                        await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
                        init = null;
                    }
                    await Navigate("github"); // Missing file and missing theme key: do not create or rewrite settings.
                    Check(!File.Exists(Path.Combine(root, "settings.json")), "First paint wrote settings");
                    foreach (var theme in new[] { "dark", "light", "github" })
                    {
                        settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        // The real selector, real WebView2 RPC, real SettingsStore and real native palette callback.
                        await Js($$"""
                            openDrawer();
                            {
                              const select=document.querySelector('#theme');
                              new MutationObserver((_,observer)=>{
                                if(!S.themeBusy){observer.disconnect();chrome.webview.postMessage('theme-test-settled');}
                              }).observe(select,{attributes:true,attributeFilter:['disabled']});
                              select.value='{{theme}}';
                              select.dispatchEvent(new Event('change',{bubbles:true}));
                            }
                            """);
                        await settled.Task.WaitAsync(TimeSpan.FromSeconds(15));
                        var saved = new SettingsStore(Path.Combine(root, "settings.json"));
                        Check(saved.Current.Theme == theme, "Selector did not persist through the native bridge");
                        Check(((SolidColorBrush)window.Background).Color == AppTheme.Color(AppTheme.Palette(theme).Canvas) &&
                            web.DefaultBackgroundColor.R == AppTheme.Color(AppTheme.Palette(theme).Canvas).R, "Native canvas did not switch");
                        Check(((SolidColorBrush)pop.Resources["Theme.Text"]).Color == AppTheme.Color(AppTheme.Palette(theme).Text), "PoP chrome palette did not switch");
                        await Navigate(theme);
                        var loaded = await Js("({theme:S.settings.theme,applied:document.documentElement.dataset.theme})");
                        Check(loaded.GetProperty("theme").GetString() == theme && loaded.GetProperty("applied").GetString() == theme, "Persisted theme did not survive native reload");
                    }
                    await Js("openDrawer();document.querySelector('#theme').focus();");
                    Check((await Js("document.activeElement.id")).GetString() == "theme", "Theme selector cannot receive keyboard focus");
                }
                catch (Exception ex) { failure = ex; }
                finally
                {
                    var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (environment is not null) environment.BrowserProcessExited += (_, _) => exited.TrySetResult();
                    pop.Dispose(); web.Dispose(); window.Close();
                    if (environment is not null) await Task.WhenAny(exited.Task, Task.Delay(10000));
                    try { Directory.Delete(root, true); }
                    catch (IOException ex) { failure ??= new Exception("Could not clean isolated theme profile", ex); }
                    if (failure is null) done.TrySetResult(); else done.TrySetException(failure);
                    Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            };
            window.Show();
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(90));
    }
}
