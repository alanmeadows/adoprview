using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AdoPrView;

static class UpdateTests
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    static readonly byte[] Payload = Encoding.UTF8.GetBytes("synthetic installer bytes");
    static readonly string Digest = "sha256:" + Convert.ToHexString(SHA256.HashData(Payload)).ToLowerInvariant();

    static string Release(string tag, string? assetName = null, string? digest = null, long? size = null, string? url = null) => new JsonObject
    {
        ["tag_name"] = tag,
        ["html_url"] = "https://github.com/alanmeadows/adoprview/releases/tag/" + tag,
        ["assets"] = new JsonArray(new JsonObject
        {
            ["name"] = assetName ?? $"AdoPrView-{tag}-win-x64-setup.exe",
            ["browser_download_url"] = url ?? $"https://github.com/alanmeadows/adoprview/releases/download/{tag}/AdoPrView-{tag}-win-x64-setup.exe",
            ["size"] = size ?? Payload.Length,
            ["digest"] = digest ?? Digest,
        }),
    }.ToJsonString();

    static UpdateService Service(string root, string release, byte[]? download = null, HttpStatusCode status = HttpStatusCode.OK,
        string appDir = @"C:\Apps\AdoPrView", string? registered = @"C:\Apps\AdoPrView\") =>
        new(new FakeHandler(request =>
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                Check(request.Headers.UserAgent.ToString() == "AdoPrView/0.1.3" &&
                    request.Headers.Accept.ToString() == "application/vnd.github+json", "Check request headers are wrong");
                return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(release) });
            }
            Check(request.RequestUri.Host == "github.com", "Download did not use the release asset URL");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(download ?? Payload) });
        }), new Version(0, 1, 3), root, appDir, () => registered);

    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("update check finds newer release and gates one-click install on registered location", () => InDir(async dir =>
        {
            var state = await Service(dir, Release("v0.2.0")).CheckAsync(default);
            Check(state is { Available: true, CanInstall: true, Latest: "0.2.0", Current: "0.1.3", Error: null, CheckedAt: not null } &&
                state.ReleaseUrl!.EndsWith("/v0.2.0"), "Newer release was not offered");
            foreach (var registered in new[] { @"D:\Elsewhere", null })
            {
                state = await Service(dir, Release("v0.2.0"), registered: registered).CheckAsync(default);
                Check(state is { Available: true, CanInstall: false, Error: null }, "Unregistered copy was offered one-click install");
            }
        }));
        await test("update check ignores equal and older releases", () => InDir(async dir =>
        {
            foreach (var tag in new[] { "v0.1.3", "v0.1.2", "v0.0.9" })
            {
                var state = await Service(dir, Release(tag)).CheckAsync(default);
                Check(state is { Available: false, CanInstall: false, Error: null } && state.Latest == tag[1..], "Non-newer release offered: " + tag);
            }
        }));
        await test("update check reports malformed releases and HTTP failures without offering an update", () => InDir(async dir =>
        {
            var cases = new (string Name, UpdateService Service)[]
            {
                ("bad tag", Service(dir, Release("0.2.0"))),
                ("prerelease tag", Service(dir, Release("v0.2.0-beta"))),
                ("missing asset", Service(dir, Release("v0.2.0", assetName: "AdoPrView-v0.2.0-win-x64.zip"))),
                ("missing digest", Service(dir, Release("v0.2.0", digest: ""))),
                ("invalid digest", Service(dir, Release("v0.2.0", digest: "sha1:abc"))),
                ("zero size", Service(dir, Release("v0.2.0", size: 0))),
                ("foreign url", Service(dir, Release("v0.2.0", url: "https://example.com/setup.exe"))),
                ("http 403", Service(dir, "{}", status: HttpStatusCode.Forbidden)),
            };
            foreach (var (name, service) in cases)
            {
                var state = await service.CheckAsync(default);
                Check(state is { Available: false, CanInstall: false, Error: not null } && service.Current == state, "No error for " + name);
                try { await service.DownloadAsync(default); Check(false, "Download allowed after " + name); }
                catch (InvalidOperationException) { }
            }
        }));
        await test("update download verifies installer, sweeps stale attempts and holds the handoff lock", () => InDir(async dir =>
        {
            var stale = Directory.CreateDirectory(Path.Combine(dir, Guid.NewGuid().ToString("N"))).FullName;
            File.WriteAllText(Path.Combine(stale, "old.exe"), "old");
            var unrelated = Directory.CreateDirectory(Path.Combine(dir, "keep")).FullName;
            var service = Service(dir, Release("v0.2.0"));
            await service.CheckAsync(default);
            var path = await service.DownloadAsync(default);
            Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(Payload) && Path.GetFileName(path) == "AdoPrView-v0.2.0-win-x64-setup.exe" &&
                Path.GetDirectoryName(Path.GetDirectoryName(path)) == dir && !Directory.Exists(stale) && Directory.Exists(unrelated),
                "Installer was not staged in a fresh attempt dir, or the sweep touched a non-attempt dir");
            foreach (var other in new[] { service, Service(dir, Release("v0.2.0")) })
            {
                await other.CheckAsync(default);
                try { await other.DownloadAsync(default); Check(false, "Second update handoff was allowed"); }
                catch (InvalidOperationException ex) { Check(ex.Message == "Another ADO PR View update is already in progress.", "Wrong busy message"); }
            }
            Check(File.Exists(path), "Rejected handoff disturbed the staged installer");
            service.Abandon(path);
            Check(!Directory.Exists(Path.GetDirectoryName(path)), "Abandoned handoff left its installer");
            var retried = await service.DownloadAsync(default);
            Check(File.Exists(retried), "Upgrade could not be retried after abandoning a handoff");
            service.Abandon(path);
            var rival = Service(dir, Release("v0.2.0"));
            await rival.CheckAsync(default);
            try { await rival.DownloadAsync(default); Check(false, "Abandoning a stale handoff released the current one"); }
            catch (InvalidOperationException) { }
            Check(File.Exists(retried), "Abandoning a stale handoff removed the current installer");
            service.Abandon(retried);
        }));
        await test("update download rejects hash or size mismatches and allows a retry", () => InDir(async dir =>
        {
            foreach (var bad in new[] { Encoding.UTF8.GetBytes("synthetic installer BYTES"), Payload[..^1] })
            {
                var service = Service(dir, Release("v0.2.0"), download: bad);
                await service.CheckAsync(default);
                try { await service.DownloadAsync(default); Check(false, "Corrupt installer accepted"); }
                catch (InvalidDataException) { }
                Check(Directory.GetDirectories(dir).Length == 0, "Corrupt installer was left on disk");
            }
            var retry = Service(dir, Release("v0.2.0"));
            await retry.CheckAsync(default);
            var staged = await retry.DownloadAsync(default);
            Check(File.Exists(staged), "Retry after failed download was blocked");
            retry.Abandon(staged);
        }));
        await test("update download rechecks the registered install location", () => InDir(async dir =>
        {
            string? registered = @"C:\Apps\AdoPrView";
            var service = new UpdateService(new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Release("v0.2.0")) })),
                new Version(0, 1, 3), dir, @"C:\Apps\AdoPrView", () => registered);
            Check((await service.CheckAsync(default)).CanInstall, "Registered copy was not installable");
            registered = @"D:\Moved";
            try { await service.DownloadAsync(default); Check(false, "Download ignored a changed install location"); }
            catch (InvalidOperationException) { }
        }));
        await test("update installer start info requests silent in-place update", () =>
        {
            var info = UpdateService.InstallerStartInfo(@"C:\Updates\abc\AdoPrView-v0.2.0-win-x64-setup.exe");
            Check(info.FileName.EndsWith("setup.exe") && info.WorkingDirectory == @"C:\Updates\abc" && !info.UseShellExecute &&
                info.ArgumentList.SequenceEqual(["/SILENT", "/NORESTART", "/UPDATE"]), "Installer arguments are wrong");
            return Task.CompletedTask;
        });
    }

    static async Task InDir(Func<string, Task> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ado-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try { await body(dir); }
        finally { Directory.Delete(dir, true); }
    }
}
