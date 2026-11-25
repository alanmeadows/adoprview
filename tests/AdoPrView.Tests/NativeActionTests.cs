using System.ComponentModel;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using AdoPrView;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

static class NativeActionTests
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("Native action browser launch failure is actionable and contains no raw shell error or URL", async () =>
        {
            Exception? observed = null;
            await NativeActions.RunAsync(() =>
            {
                NativeActions.OpenBrowser("https://example.invalid/pr/1", info =>
                {
                    Check(info.UseShellExecute && info.FileName == "https://example.invalid/pr/1" &&
                        info.Arguments == "", "Browser launch did not use an isolated HTTPS target");
                    throw new Win32Exception(1155, "SECRET shell failure https://private.invalid");
                });
                return Task.CompletedTask;
            }, ex => observed = ex);
            Check(observed is InvalidOperationException && observed.Message.Contains("1155") &&
                observed.Message.Contains("default HTTPS app") && !observed.Message.Contains("SECRET") &&
                !observed.Message.Contains("private.invalid"), "Native launch failure escaped or leaked shell details");
            var launched = false;
            foreach (var url in new[] { "https://", "file:///C:/bad", "javascript:alert(1)", "https://example.invalid/\n" })
            {
                try { NativeActions.OpenBrowser(url, _ => launched = true); throw new Exception("Invalid browser target accepted"); }
                catch (ArgumentException) { }
            }
            Check(!launched, "Invalid target reached Windows shell");
        });

        await test("Native action bridge rejects malformed IDs and observes failed delivery only once", async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "ado-action-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var store = new SettingsStore(Path.Combine(directory, "settings.json"));
                var prs = new PrService(new AdoClient(), store);
                var replies = new List<string>();
                var bridge = new Bridge(prs, store, text => { replies.Add(text); return Task.CompletedTask; }, _ => { },
                    openBrowser: url => NativeActions.OpenBrowser(url, _ => throw new Win32Exception(1155, "SECRET")));
                await bridge.HandleAsync("""{"id":7,"method":"openUrl","params":{"url":"https://example.invalid/pr"}}""");
                var response = JsonNode.Parse(replies.Single())!;
                Check(response["id"]!.GetValue<int>() == 7 && response["error"]!.GetValue<string>().Contains("1155"),
                    "Browser action did not return an RPC error to its captured request");
                foreach (var message in new[] { "[]", "null", """{"id":{},"method":"refresh"}""", """{"id":"bad"}""" })
                    await bridge.HandleAsync(message);
                Check(replies.Count == 5 && replies.All(text => !text.Contains("SECRET")), "Malformed request escaped or leaked data");
                var attempts = 0;
                var reports = new List<Exception>();
                var broken = new Bridge(prs, store, _ => { attempts++; throw new IOException("Synthetic disconnected dashboard"); }, _ => { });
                await NativeActions.RunAsync(() => broken.HandleAsync("""{"id":8,"method":"refresh"}"""), reports.Add);
                Check(attempts == 1 && reports.Single() is IOException,
                    "Failed response delivery was retried or left as an unobserved task");
            }
            finally { Directory.Delete(directory, recursive: true); }
        });
        await test("Native update bridge routes init state, manual check and install to the update action", async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "ado-action-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var store = new SettingsStore(Path.Combine(directory, "settings.json"));
                var prs = new PrService(new AdoClient(), store);
                var replies = new List<string>();
                var actions = new List<string>();
                var bridge = new Bridge(prs, store, text => { replies.Add(text); return Task.CompletedTask; }, _ => { },
                    updateAction: (action, _) => { actions.Add(action); return Task.FromResult<object>(new { action }); });
                foreach (var method in new[] { "init", "checkForUpdate", "installUpdate" })
                    await bridge.HandleAsync($$"""{"id":1,"method":"{{method}}"}""");
                Check(actions.SequenceEqual(["updateState", "checkForUpdate", "installUpdate"]) &&
                    JsonNode.Parse(replies[0])!["result"]!["update"]!["action"]!.GetValue<string>() == "updateState" &&
                    JsonNode.Parse(replies[2])!["result"]!["action"]!.GetValue<string>() == "installUpdate", "Update RPCs were not routed");
                replies.Clear();
                await new Bridge(prs, store, text => { replies.Add(text); return Task.CompletedTask; }, _ => { })
                    .HandleAsync("""{"id":2,"method":"installUpdate"}""");
                Check(JsonNode.Parse(replies.Single())!["error"]!.GetValue<string>() == "Updates are unavailable.", "Missing update action was not an RPC error");
            }
            finally { Directory.Delete(directory, recursive: true); }
        });
        await test("Native init reads the latest whole snapshot after asynchronous run lists and serializes publication revisions", async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "ado-action-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                var store = new SettingsStore(Path.Combine(directory, "settings.json"));
                var prs = new PrService(new AdoClient(), store);
                var publish = typeof(PrService).GetMethod("Publish", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var earlier = new Snapshot { OrderRevision = 3, Error = "earlier synthetic refresh state" };
                publish.Invoke(prs, [earlier]);
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                string? reply = null;
                var bridge = new Bridge(prs, store, text => { reply = text; return Task.CompletedTask; }, _ => { },
                    copilotAction: async (action, _) =>
                    {
                        Check(action == "listCopilot", "Init used an unexpected run action");
                        entered.TrySetResult();
                        await release.Task.WaitAsync(TimeSpan.FromSeconds(3));
                        return Array.Empty<object>();
                    });
                var init = bridge.HandleAsync("""{"id":42,"method":"init"}""");
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var latest = new Snapshot { OrderRevision = 3, Error = "newer synthetic refresh state" };
                publish.Invoke(prs, [latest]);
                release.TrySetResult();
                await init.WaitAsync(TimeSpan.FromSeconds(3));
                var result = JsonNode.Parse(reply!)!["result"]!["snapshot"]!;
                Check(latest.Revision > earlier.Revision &&
                    result["revision"]!.GetValue<long>() == latest.Revision &&
                    result["error"]!.GetValue<string>() == latest.Error &&
                    result["orderRevision"]!.GetValue<long>() == earlier.OrderRevision,
                    "Init serialized an obsolete whole snapshot or publication revision changed the row order");
            }
            finally { release.TrySetResult(); Directory.Delete(directory, recursive: true); }
        });
        if (Environment.GetEnvironmentVariable("ADOPRVIEW_TEST_NATIVE_ACTION") == "1")
            await test("Native action real WPF WebView2 popup launch failure remains handled and leaves dashboard visible", NativeBrowser);
    }

    static Task NativeBrowser()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var profile = Path.Combine(Path.GetTempPath(), "ado-action-browser-" + Guid.NewGuid().ToString("N"));
            var browser = new WebView2();
            var window = new Window { Width = 900, Height = 500, Content = browser, ShowInTaskbar = false };
            window.Loaded += async (_, _) =>
            {
                Exception? failure = null;
                CoreWebView2Environment? environment = null;
                var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                try
                {
                    environment = await CoreWebView2Environment.CreateAsync(null, profile);
                    environment.BrowserProcessExited += (_, _) => exited.TrySetResult();
                    await browser.EnsureCoreWebView2Async(environment);
                    var observed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var handled = false;
                    browser.CoreWebView2.NewWindowRequested += (_, e) =>
                    {
                        _ = NativeActions.OpenNewWindowAsync(e, ex => observed.TrySetResult(ex),
                            _ => throw new Win32Exception(1155, "SECRET shell failure"));
                        handled = e.Handled;
                    };
                    var navigated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    browser.CoreWebView2.NavigationCompleted += (_, _) => navigated.TrySetResult();
                    browser.NavigateToString("<!doctype html><title>Synthetic dashboard</title><p>No live PR actions</p>");
                    await navigated.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    await browser.CoreWebView2.ExecuteScriptAsync("window.open('https://example.invalid/synthetic-pr','_blank');");
                    var error = await observed.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    Check(handled && window.IsVisible && window.IsLoaded && browser.CoreWebView2 is not null &&
                        error.Message.Contains("default HTTPS app"), "Browser failure closed/hid the dashboard or escaped event routing");
                    Check((await browser.CoreWebView2!.ExecuteScriptAsync("document.title")).Contains("Synthetic dashboard"),
                        "Dashboard no longer responds after failed native action");
                }
                catch (Exception ex) { failure = ex; }
                finally
                {
                    browser.Dispose();
                    window.Close();
                    try
                    {
                        if (environment is not null) await exited.Task.WaitAsync(TimeSpan.FromSeconds(15));
                        if (Directory.Exists(profile)) Directory.Delete(profile, recursive: true);
                    }
                    catch (Exception ex) { failure ??= ex; }
                    if (failure is null) done.TrySetResult(); else done.TrySetException(failure);
                    Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            };
            window.Show();
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }
}
