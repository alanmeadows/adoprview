using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace AdoPrView;

public sealed class AdoException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

sealed class TokenProvider
{
    const string AdoResource = "499b84ac-1321-427f-aa17-267ca6975798";
    readonly SemaphoreSlim _lock = new(1, 1);
    AuthenticationHeaderValue? _header;
    DateTimeOffset _expires;

    public async Task<AuthenticationHeaderValue> GetAsync(bool force = false)
    {
        await _lock.WaitAsync();
        try
        {
            if (!force && _header != null && DateTimeOffset.UtcNow < _expires) return _header;
            var pat = Environment.GetEnvironmentVariable("AZURE_DEVOPS_EXT_PAT") ?? Environment.GetEnvironmentVariable("AZURE_DEVOPS_PAT");
            if (!string.IsNullOrWhiteSpace(pat))
            {
                _header = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + pat)));
                _expires = DateTimeOffset.MaxValue;
                return _header;
            }
            var psi = new ProcessStartInfo("cmd.exe", $"/d /c az account get-access-token --resource {AdoResource} -o json")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            var stdout = await p.StandardOutput.ReadToEndAsync();
            var stderr = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            if (p.ExitCode != 0)
                throw new AdoException("Azure CLI could not get an ADO token. Run 'az login'. " + stderr.Trim());
            var j = JsonNode.Parse(stdout)!;
            _header = new AuthenticationHeaderValue("Bearer", j["accessToken"]!.GetValue<string>());
            var exp = j["expires_on"]?.GetValue<long?>();
            _expires = exp is long e ? DateTimeOffset.FromUnixTimeSeconds(e).AddMinutes(-5) : DateTimeOffset.UtcNow.AddMinutes(40);
            return _header;
        }
        finally { _lock.Release(); }
    }
}

public sealed class AdoClient
{
    readonly HttpClient _http;
    readonly Func<bool, Task<AuthenticationHeaderValue>> _getToken;
    readonly SemaphoreSlim _throttle = new(8, 8);

    public AdoClient() : this(new HttpClient(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5), AutomaticDecompression = DecompressionMethods.All, AllowAutoRedirect = false,
    }) { Timeout = TimeSpan.FromSeconds(60) }, new TokenProvider().GetAsync) { }

    internal AdoClient(HttpClient http, Func<bool, Task<AuthenticationHeaderValue>> getToken)
    {
        _http = http;
        _getToken = getToken;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AdoPrView/0.1");
    }

    public Task<JsonNode> GetAsync(string url, CancellationToken ct = default) => SendJsonAsync(HttpMethod.Get, url, null, ct);
    public Task<JsonNode> PatchAsync(string url, JsonNode body, CancellationToken ct = default) => SendJsonAsync(HttpMethod.Patch, url, body, ct);
    internal Task<JsonNode> PatchOnceAsync(string url, JsonNode body) => SendJsonAsync(HttpMethod.Patch, url, body, default, retry: false);
    public async Task DeleteAsync(string url, CancellationToken ct = default)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, url), ct, retry: false);
    }

    public async Task<string> GetTextAsync(string url, CancellationToken ct = default)
    {
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url) { Headers = { Accept = { new("text/plain") } } }, ct);
        return await resp.Content.ReadAsStringAsync(ct);
    }

    async Task<JsonNode> SendJsonAsync(HttpMethod method, string url, JsonNode? body, CancellationToken ct, bool retry = true)
    {
        using var resp = await SendAsync(() =>
        {
            var req = new HttpRequestMessage(method, url);
            req.Headers.Accept.Add(new("application/json"));
            if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            return req;
        }, ct, retry);
        var text = await resp.Content.ReadAsStringAsync(ct);
        return string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text)!;
    }

    async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build, CancellationToken ct, bool retry = true)
    {
        await _throttle.WaitAsync(ct);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var req = build();
                req.Headers.Authorization = await _getToken(attempt > 0);
                var resp = await _http.SendAsync(req, ct);
                if (resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.NonAuthoritativeInformation) return resp;
                var status = resp.StatusCode;
                if (retry && attempt == 0 && status is HttpStatusCode.Unauthorized or HttpStatusCode.NonAuthoritativeInformation)
                {
                    resp.Dispose();
                    continue;
                }
                if (retry && attempt < 2 && ((int)status == 429 || (int)status >= 500))
                {
                    var delay = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2 * (attempt + 1));
                    resp.Dispose();
                    await Task.Delay(delay, ct);
                    continue;
                }
                var err = await resp.Content.ReadAsStringAsync(ct);
                resp.Dispose();
                string msg;
                try { msg = JsonNode.Parse(err)?["message"]?.GetValue<string>() ?? err; } catch { msg = err; }
                if (msg.Length > 400) msg = msg[..400];
                throw new AdoException($"{(int)status} {status}: {msg}", status);
            }
        }
        finally { _throttle.Release(); }
    }
}
