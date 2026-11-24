using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AdoPrView;

public sealed record UpdateState(string Current, string? Latest, bool Available, bool CanInstall, string? ReleaseUrl, DateTimeOffset? CheckedAt, string? Error);

public sealed partial class UpdateService
{
    const string LatestUrl = "https://api.github.com/repos/alanmeadows/adoprview/releases/latest";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{5D2F66AD-977D-4B94-BD69-094B8FD6E532}_is1";
    const string BusyMessage = "Another ADO PR View update is already in progress.";

    sealed record Installer(string Name, Uri Url, long Size, string Sha256);

    readonly HttpClient _http;
    readonly Version _current;
    readonly string _root;
    readonly string _appDir;
    readonly Func<string?> _installLocation;
    readonly object _gate = new();
    Installer? _installer;
    FileStream? _lock;

    public UpdateService(HttpMessageHandler? handler = null, Version? current = null, string? updatesRoot = null,
        string? appDir = null, Func<string?>? installLocation = null)
    {
        _http = new HttpClient(handler ?? new SocketsHttpHandler()) { Timeout = TimeSpan.FromMinutes(10) };
        var v = current ?? typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0);
        _current = new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        _root = updatesRoot ?? Path.Combine(SettingsStore.Dir, "Updates");
        _appDir = appDir ?? AppContext.BaseDirectory;
        _installLocation = installLocation ?? (() => Registry.CurrentUser.OpenSubKey(UninstallKey)?.GetValue("InstallLocation") as string);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AdoPrView/" + _current.ToString(3));
        Current = new(_current.ToString(3), null, false, false, null, null, null);
    }

    public UpdateState Current { get; private set; }

    [GeneratedRegex("^v([0-9]{1,9})\\.([0-9]{1,9})\\.([0-9]{1,9})$")]
    private static partial Regex TagPattern();

    [GeneratedRegex("^sha256:([0-9a-fA-F]{64})$")]
    private static partial Regex DigestPattern();

    public async Task<UpdateState> CheckAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        string? latest = null, releaseUrl = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await _http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"GitHub returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
            var release = JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token))!;
            var tag = (string?)release["tag_name"] ?? "";
            var match = TagPattern().Match(tag);
            if (!match.Success) throw new InvalidOperationException($"Latest release tag '{tag}' is not in vMAJOR.MINOR.PATCH form.");
            var version = new Version(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value));
            latest = version.ToString(3);
            releaseUrl = (string?)release["html_url"];
            _installer = null;
            if (version <= _current) return Current = new(_current.ToString(3), latest, false, false, releaseUrl, now, null);
            _installer = FindInstaller(release, tag);
            return Current = new(_current.ToString(3), latest, true, IsInstalledCopy(), releaseUrl, now, null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _installer = null;
            var error = ex is OperationCanceledException ? "Timed out checking for updates." : "Update check failed: " + ex.Message;
            return Current = new(_current.ToString(3), latest, false, false, releaseUrl, now, error);
        }
    }

    static Installer FindInstaller(JsonNode release, string tag)
    {
        var name = $"AdoPrView-{tag}-win-x64-setup.exe";
        var asset = release["assets"]?.AsArray().FirstOrDefault(a => (string?)a?["name"] == name)
            ?? throw new InvalidOperationException($"Release {tag} has no {name} asset.");
        if (!Uri.TryCreate((string?)asset["browser_download_url"], UriKind.Absolute, out var url) ||
            url.Scheme != Uri.UriSchemeHttps || !url.IsDefaultPort || !string.Equals(url.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Release asset {name} does not have an https://github.com download URL.");
        var size = asset["size"]?.GetValue<long>() ?? 0;
        if (size <= 0) throw new InvalidOperationException($"Release asset {name} has no size.");
        var digest = DigestPattern().Match((string?)asset["digest"] ?? "");
        if (!digest.Success) throw new InvalidOperationException($"Release asset {name} has no SHA-256 digest.");
        return new(name, url, size, digest.Groups[1].Value);
    }

    bool IsInstalledCopy()
    {
        try
        {
            var registered = _installLocation();
            return !string.IsNullOrWhiteSpace(registered) && string.Equals(Normalize(registered), Normalize(_appDir), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            return false;
        }
        static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    public async Task<string> DownloadAsync(CancellationToken ct)
    {
        var installer = _installer;
        if (!Current.Available || !Current.CanInstall || installer is null)
            throw new InvalidOperationException("No installable update is available.");
        lock (_gate)
        {
            if (_lock is not null) throw new InvalidOperationException(BusyMessage);
            Directory.CreateDirectory(_root);
            try { _lock = new FileStream(Path.Combine(_root, "update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { throw new InvalidOperationException(BusyMessage); }
        }
        var attempt = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var dir in Directory.GetDirectories(_root))
                try { Directory.Delete(dir, true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            Directory.CreateDirectory(attempt);
            var path = Path.Combine(attempt, installer.Name);
            using (var response = await _http.GetAsync(installer.Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > installer.Size) break;
                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                if (total != installer.Size)
                    throw new InvalidDataException($"Downloaded installer size does not match the release ({installer.Size} bytes expected).");
                if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), installer.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Downloaded installer SHA-256 does not match the release digest.");
            }
            return path;
        }
        catch
        {
            try { Directory.Delete(attempt, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            lock (_gate) { _lock?.Dispose(); _lock = null; }
            throw;
        }
    }

    public static ProcessStartInfo InstallerStartInfo(string installerPath)
    {
        var info = new ProcessStartInfo(installerPath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(installerPath)! };
        foreach (var arg in new[] { "/SILENT", "/NORESTART", "/UPDATE" }) info.ArgumentList.Add(arg);
        return info;
    }
}
