using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using AdoPrView;
using Microsoft.Web.WebView2.Core;

static class PopPaneTests
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static PrView Pr(int id = 123) => new()
    {
        Key = "repo-id:" + id, Id = id, Title = "Offline PR " + id, Url = "https://untrusted.invalid/wrong-row",
        Org = "offline", Project = "project", ProjectId = "project-id", Repo = "repo", RepoId = "repo-id",
        Author = new("Offline", null, null),
    };

    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("PoP captures exact identity and builds its own ADO URL, never the frontend's supplied URL", () =>
        {
            var target = PopTarget.FromPr(Pr());
            Check(target.Url == "https://dev.azure.com/offline/project/_git/repo/pullrequest/123", "Untrusted URL or wrong PR");
            Check(target.Label.Contains("!123") && target.Label.Contains("Offline PR"), "Visible identity missing");
            Check((target with { Project = "project with space", Repo = "repo/#?" }).Url.Contains("project%20with%20space/_git/repo%2F%23%3F/"), "Unescaped URL segments");
            return Task.CompletedTask;
        });
        await test("PoP navigation cannot reach dashboard/local/non-HTTPS privileges; origin omits auth query/fragment", () =>
        {
            foreach (var url in new[] { "https://dev.azure.com/offline", "https://login.microsoftonline.com/common", "https://federation.example/authorize" })
                Check(PopTarget.AllowsNavigation(url), "HTTPS authentication navigation blocked");
            foreach (var url in new[] { "https://app.local/index.html", "https://APP.LOCAL./index.html", "https://sub.app.local", "http://example.com", "file:///C:/test", "javascript:alert(1)", "https://localhost", "https://127.0.0.1", "https://user:password@example.com", "msauth://test", "about:blank" })
                Check(!PopTarget.AllowsNavigation(url), "Unsafe navigation allowed: " + url);
            Check(PopTarget.AllowsNavigation("about:blank", allowBlank: true), "Blank popup initialization rejected");
            Check(PopTarget.Origin("https://login.microsoftonline.com/path?code=offline#token=offline") == "https://login.microsoftonline.com", "Auth material in origin display");
            Check(PopTarget.IsDashboardSource("https://app.local/index.html?mock=1"), "Dashboard source rejected");
            Check(!PopTarget.IsDashboardSource("https://dev.azure.com/index.html") && !PopTarget.IsDashboardSource("https://app.local/other"), "Untrusted RPC source allowed");
            return Task.CompletedTask;
        });
        await test("PoP selection, close/reopen and late refresh remain captured to the original PR", async () =>
        {
            var a = PopTarget.FromPr(Pr());
            var b = PopTarget.FromPr(Pr(456));
            var pending = new TaskCompletionSource<PopPolicyResult>();
            var calls = new List<PopTarget>();
            var session = new PopSession(target => { calls.Add(target); return pending.Task; });
            var captured = session.Open(a);
            Check(!calls.Any() && !session.Status.Contains("satisfied"), "Opening inferred approval");
            var read = session.CheckAsync(captured);
            session.Close();
            session.Open(b);
            pending.SetResult(new("ok", "ADO reports PoP satisfied for A."));
            await read;
            Check(calls.Single() == a && session.Selection!.Target == b && !session.Status.Contains("satisfied"), "Old result relabelled another PR");
            session.Close();
            var again = session.Open(a);
            Check(again.Revision > captured.Revision && !session.Status.Contains("satisfied"), "Reopen silently reused an unverified result");
        });
        await test("PoP duplicate checks are guarded; failures stay explicit without manufacturing success", async () =>
        {
            var pending = new TaskCompletionSource<PopPolicyResult>();
            var session = new PopSession(_ => pending.Task);
            var captured = session.Open(PopTarget.FromPr(Pr()));
            var first = session.CheckAsync(captured);
            try { await session.CheckAsync(captured); throw new Exception("Duplicate check accepted"); }
            catch (InvalidOperationException) { }
            pending.SetException(new IOException("Offline transport failure"));
            try { await first; throw new Exception("Failed check succeeded"); }
            catch (IOException) { }
            Check(!session.IsChecking && session.Status.Contains("Could not read") && !session.Status.Contains("satisfied"), "Failure/guard state lost");
        });
        await test("Only real nonexpired PoP policy results can report satisfied; absence is not success", () =>
        {
            var pr = Pr();
            Check(PopPolicyResult.FromPr(pr).State == "unavailable", "No policy became approved");
            foreach (var (status, expired, state) in new[] { ("queued", false, "pending"), ("rejected", false, "pending"), ("approved", true, "pending"), ("approved", false, "ok"), ("notApplicable", false, "ok") })
            {
                pr.Policies.Clear();
                pr.Policies.Add(new("evaluation", "pop", "Proof of Presence", status, true, null, null, expired));
                Check(PopPolicyResult.FromPr(pr).State == state, "Incorrect PoP interpretation: " + status);
            }
            return Task.CompletedTask;
        });
        await test("PoP native RPC passes only the captured key; unsupported hosts fail rather than opening external tabs", async () =>
        {
            var store = new SettingsStore(new AppSettings());
            var service = new PrService(new AdoClient(), store);
            string? captured = null, reply = null;
            var bridge = new Bridge(service, store, text => { reply = text; return Task.CompletedTask; }, _ => { },
                popAction: key => { captured = key; return Task.FromResult<object>(new { message = "Offline native pane" }); });
            await bridge.HandleAsync("""{"id":1,"method":"openPop","params":{"key":"repo-id:123","url":"https://untrusted.invalid/"}}""");
            Check(captured == "repo-id:123" && reply!.Contains("Offline native pane"), "RPC did not bind clicked key");
            bridge = new Bridge(service, store, text => { reply = text; return Task.CompletedTask; }, _ => { });
            await bridge.HandleAsync("""{"id":2,"method":"openPop","params":{"key":"repo-id:123"}}""");
            Check(reply!.Contains("unavailable") && reply.Contains("error"), "Missing native host faked browser success");
        });
        await test("PoP Check status reads captured PR and actual policies, updates gates without reordering, and rejects swapped identity", ReadPolicy);
        if (Environment.GetEnvironmentVariable("ADOPRVIEW_TEST_POP_WEBVIEW") == "1")
            await test("Native PoP WebView2 reuses isolated surface, handles inline auth popup and navigation failure with intercepted fake pages", NativeBrowser);
    }

    static async Task ReadPolicy()
    {
        var target = PopTarget.FromPr(Pr());
        var state = "queued";
        var wrong = false;
        using var http = new HttpClient(new FakeHandler(request =>
        {
            Check(request.Method == HttpMethod.Get, "PoP check modified ADO");
            var path = request.RequestUri!.AbsolutePath;
            string json;
            if (path.EndsWith("/connectionData")) json = """{"authenticatedUser":{"id":"offline","providerDisplayName":"Offline"}}""";
            else if (path.EndsWith("/pullrequests/123"))
                json = $$$$"""{"pullRequestId":{{{{(wrong ? 999 : 123)}}}},"status":"active","title":"Offline PR","sourceRefName":"refs/heads/fix","targetRefName":"refs/heads/main","mergeStatus":"succeeded","repository":{"id":"repo-id","name":"repo","project":{"id":"project-id"}}}""";
            else if (path.EndsWith("/evaluations"))
                json = $$$$"""{"value":[{"evaluationId":"evaluation","status":"{{{{state}}}}","configuration":{"isBlocking":true,"isEnabled":true,"type":{"id":"67ed70bd-2a6b-4006-af44-be590463f46d","displayName":"Proof of Presence"},"settings":{}}}]}""";
            else if (path.EndsWith("/refs")) json = """{"value":[{"name":"refs/heads/main","objectId":"1111111111111111111111111111111111111111"}]}""";
            else if (path.EndsWith("/threads") || path.EndsWith("/iterations")) json = """{"value":[]}""";
            else throw new Exception("Unexpected PoP read: " + path);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }));
        var store = new SettingsStore(new AppSettings { Repos = [new RepoRef { Org = "offline", Project = "project", Repo = "repo" }] });
        var service = new PrService(new AdoClient(http, _ => Task.FromResult(new AuthenticationHeaderValue("Bearer", "offline"))), store);
        typeof(PrService).GetField("_snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, new Snapshot { Prs = [Pr()], OrderRevision = 7 });
        Check(service.GetPopTarget(target.Key) == target, "Native target drifted");
        Check((await service.RefreshPopAsync(target)).State == "pending" && service.Find(target.Key).HumanActions.Contains("pop"), "Pending policy hidden");
        state = "approved";
        Check((await service.RefreshPopAsync(target)).State == "ok" && service.Find(target.Key).Gates["pop"].State == "ok", "Refreshed approved policy was not consumed");
        Check(service.Current.OrderRevision == 7, "PoP refresh reordered rows");
        // A captured pane remains usable even if filtering/removal changed the dashboard membership.
        typeof(PrService).GetField("_snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, new Snapshot { OrderRevision = 7 });
        Check((await service.RefreshPopAsync(target)).State == "ok" && service.Current.Prs.Count == 0, "Removed row was retargeted or resurrected");
        wrong = true;
        try { await service.RefreshPopAsync(target); throw new Exception("Swapped identity accepted"); }
        catch (InvalidOperationException ex) { Check(ex.Message.Contains("identity"), "Wrong identity failure"); }
    }

    static Task NativeBrowser()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "ado-pop-browser-" + Guid.NewGuid().ToString("N"));
            var result = new PopPolicyResult("pending", "Offline ADO says PoP is still pending.");
            var reads = new List<PopTarget>();
            var pane = new PopBrowserPane(target => { reads.Add(target); return Task.FromResult(result); }, directory);
            var layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(170) });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var dashboard = new TextBlock
            {
                Text = "ADO PR View   /   Offline dashboard\n\nNeeds Human   !456   Offline PR 456\n\nThe table stays available above the native PoP pane.",
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xb4, 0xba, 0xc8)),
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"), FontSize = 13, Margin = new Thickness(20),
            };
            layout.Children.Add(dashboard);
            var divider = PopBrowserPane.CreateDivider();
            Grid.SetRow(divider, 1); layout.Children.Add(divider);
            Grid.SetRow(pane, 2); layout.Children.Add(pane);
            var window = new Window { Title = "ADO PR View - offline PoP fixture", Width = 1250, Height = 900, Content = layout,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0e, 0x10, 0x14)) };
            var surfaces = new List<CoreWebView2>();
            var popupReady = new TaskCompletionSource<CoreWebView2>();
            pane.SurfaceCreated += core =>
            {
                surfaces.Add(core);
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += (_, e) =>
                {
                    var error = e.Request.Uri.EndsWith("/offline-error");
                    var html = "<!doctype html><html><body style='background:#e8eef5;font:18px sans-serif;padding:24px'><h2>Offline PoP compatibility fixture</h2><p>No live sign-in, approval or credentials.</p><button onclick=\"window.open('https://login.microsoftonline.com/offline-popup')\">Open fake sign-in popup</button></body></html>";
                    e.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(html)), error ? 503 : 200, error ? "Unavailable" : "OK", "Content-Type: text/html; charset=utf-8");
                };
                core.NewWindowRequested += (_, e) => Check(e.Handled, "Popup escaped into an external window");
                if (surfaces.Count > 1) popupReady.TrySetResult(core);
            };
            window.Loaded += async (_, _) =>
            {
                Exception? failure = null;
                try
                {
                    var target = PopTarget.FromPr(Pr());
                    await pane.EnsureInitializedAsync();
                    var core = pane.Browser.CoreWebView2;
                    await Navigate(core, () => pane.OpenAsync(target));
                    Check(!core.Settings.IsWebMessageEnabled && !core.Settings.AreHostObjectsAllowed && !core.Settings.AreDevToolsEnabled, "Remote page inherited privileged capabilities");
                    var message = new TaskCompletionSource();
                    core.WebMessageReceived += (_, _) => message.TrySetResult();
                    await core.ExecuteScriptAsync("try { window.chrome.webview.postMessage('offline-canary'); } catch (_) {}");
                    Check(await Task.WhenAny(message.Task, Task.Delay(150)) != message.Task, "Remote page can dispatch native messages");
                    Check(reads.Count == 0 && pane.Session.Status.Contains("not been checked"), "Navigation inferred approval");
                    Check(pane.CurrentOrigin == "Current origin: https://dev.azure.com", "Origin identity not visible");
                    await pane.CheckAsync();
                    Check(reads.Single() == target && pane.Session.Status.Contains("pending"), "Policy check not captured");
                    await core.ExecuteScriptAsync("window.open('https://login.microsoftonline.com/offline-popup');");
                    var child = await popupReady.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    Check(pane.Popup is not null && child.Environment.UserDataFolder == core.Environment.UserDataFolder &&
                        !child.Settings.IsWebMessageEnabled && !child.Settings.AreHostObjectsAllowed, "Popup isolation/session mismatch");
                    pane.ClosePopup();
                    await Navigate(core, () => { core.Navigate("https://dev.azure.com/offline-error"); return Task.CompletedTask; });
                    Check(pane.NavigationStatus.Contains("503"), "Navigation error hidden");
                    result = new("ok", "Offline ADO reports PoP satisfied.");
                    await pane.CloseAsync();
                    Check(pane.Visibility == Visibility.Collapsed && reads.Count == 2, "Close did not refresh actual policy");
                    await Navigate(core, () => pane.OpenAsync(PopTarget.FromPr(Pr(456))));
                    Check(ReferenceEquals(core, pane.Browser.CoreWebView2) && pane.Session.Selection!.Target.Id == 456, "Reopen did not reuse native pane with new identity");
                    var shot = Environment.GetEnvironmentVariable("ADOPRVIEW_POP_SCREENSHOT");
                    if (!string.IsNullOrEmpty(shot))
                    {
                        await using var file = File.Create(shot);
                        await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
                        CaptureWindow(window, Path.ChangeExtension(shot, ".native.png"));
                        window.Width = 900; window.Height = 650;
                        window.UpdateLayout();
                        Check(pane.Browser.ActualWidth > 700 && pane.Browser.ActualHeight > 120, "Framing crowds out the narrow browser viewport");
                        Check(divider.Focusable && divider.KeyboardIncrement > 0, "Resize affordance is not keyboard accessible");
                        CaptureWindow(window, Path.ChangeExtension(shot, ".narrow.native.png"));
                    }
                }
                catch (Exception ex) { failure = ex; }
                finally
                {
                    var exited = new TaskCompletionSource();
                    var environment = pane.Browser.CoreWebView2?.Environment;
                    if (environment is not null) environment.BrowserProcessExited += (_, _) => exited.TrySetResult();
                    pane.Dispose(); window.Close();
                    if (environment is not null) await Task.WhenAny(exited.Task, Task.Delay(10000));
                    try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
                    catch (IOException ex) { failure ??= new Exception("Could not clean the synthetic PoP browser profile.", ex); }
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

    static async Task Navigate(CoreWebView2 core, Func<Task> navigate)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs e) => ready.TrySetResult();
        core.NavigationCompleted += Completed;
        try { await navigate(); await ready.Task.WaitAsync(TimeSpan.FromSeconds(20)); }
        finally { core.NavigationCompleted -= Completed; }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    static void CaptureWindow(Window window, string path)
    {
        var handle = new WindowInteropHelper(window).Handle;
        Check(GetWindowRect(handle, out var rect), "Could not read native fixture bounds");
        using var bitmap = new System.Drawing.Bitmap(rect.Right - rect.Left, rect.Bottom - rect.Top);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        var dc = graphics.GetHdc();
        try { Check(PrintWindow(handle, dc, 2), "Could not capture native PoP fixture"); }
        finally { graphics.ReleaseHdc(dc); }
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
}
