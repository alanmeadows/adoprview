using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Web.WebView2.Core;

namespace AdoPrView;

internal static class NativeActions
{
    internal static Task OpenNewWindowAsync(CoreWebView2NewWindowRequestedEventArgs request, Action<Exception> report,
        Action<ProcessStartInfo>? start = null)
    {
        request.Handled = true;
        var url = request.Uri;
        return RunAsync(() =>
        {
            if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) OpenBrowser(url, start);
            return Task.CompletedTask;
        }, report);
    }

    internal static void OpenBrowser(string url, Action<ProcessStartInfo>? start = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrEmpty(uri.Host) || url.Any(char.IsControl))
            throw new ArgumentException("Only absolute HTTPS URLs can be opened.");
        try
        {
            var info = new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true };
            if (start is not null) start(info);
            else Process.Start(info);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            var detail = ex is Win32Exception win32 ? $" (Windows error {win32.NativeErrorCode})" : "";
            throw new InvalidOperationException($"Windows could not open the default browser{detail}. " +
                "Check the default HTTPS app in Windows Settings, or copy the PR URL and open it manually.", ex);
        }
    }

    // Observe exceptions at native event/fire-and-forget boundaries, not in the service operation itself.
    internal static async Task RunAsync(Func<Task> action, Action<Exception> report)
    {
        try { await action(); }
        catch (Exception ex) { report(ex); }
    }
}
