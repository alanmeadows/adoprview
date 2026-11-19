using System.IO;
using Microsoft.Web.WebView2.Core;

namespace AdoPrView;

internal static class VideoMedia
{
    public static void Attach(CoreWebView2 core, VideoArtifacts artifacts, Action<Exception> report)
    {
        // Do not map this host to a folder: WebView2 bypasses request interception for mapped hosts.
        core.AddWebResourceRequestedFilter("https://pr-video.local/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            if (!e.Request.Uri.StartsWith("https://pr-video.local/", StringComparison.Ordinal)) return;
            try
            {
                var range = e.Request.Headers.Contains("Range") ? e.Request.Headers.GetHeader("Range") : null;
                var media = artifacts.Open(e.Request.Uri, e.Request.Method, range);
                // WebView2 retains the content stream for the response lifetime.
                e.Response = core.Environment.CreateWebResourceResponse(media.Body, media.Status, media.Reason, media.Headers);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                report(ex);
                e.Response = core.Environment.CreateWebResourceResponse(null, 404, "Not Found",
                    "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n");
            }
        };
    }
}
