using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AdoPrView;

public sealed record SpeechConnection(string ResourceId, string Endpoint, string Region, string Voice);
internal sealed record SpeechSubscription(string Id, string Name);
internal sealed record SpeechCandidate(string ResourceId, string Name, string Region, string Endpoint,
    string[] Voices, string Message)
{
    public bool Compatible => Voices.Length > 0;
}

internal sealed class AzureSpeech : IDisposable
{
    readonly HttpClient _http;
    readonly Func<string[], CancellationToken, Task<string>> _az;
    readonly SemaphoreSlim _tokenGate = new(1, 1);
    string? _token, _subscription;
    DateTimeOffset _expiry;
    internal AzureSpeech(HttpClient? http = null, Func<string[], CancellationToken, Task<string>>? az = null)
    {
        _http = http ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromMinutes(2) };
        _az = az ?? RunAzAsync;
    }

    static string Value(JsonNode node, string key) => node[key]?.GetValue<string>() ?? "";
    internal static Match Resource(string id)
    {
        var match = Regex.Match(id, @"\A/subscriptions/([a-fA-F0-9-]{36})/resourceGroups/([a-zA-Z0-9_.()-]{1,90})/providers/Microsoft\.CognitiveServices/accounts/([a-zA-Z0-9-]{2,64})\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success || !Guid.TryParse(match.Groups[1].Value, out _))
            throw new ArgumentException("Select an Azure Cognitive Services account, not a Foundry project or model deployment.");
        return match;
    }

    internal static void Validate(SpeechConnection connection)
    {
        Resource(connection.ResourceId);
        if (!Regex.IsMatch(connection.Endpoint, @"\Ahttps://[a-zA-Z0-9-]{2,64}\.cognitiveservices\.azure\.com/?\z") ||
            !Regex.IsMatch(connection.Region, @"\A[a-z0-9]{2,40}\z") ||
            !Regex.IsMatch(connection.Voice, @"\A[a-zA-Z0-9][a-zA-Z0-9:._-]{1,100}\z"))
            throw new InvalidDataException("Invalid Azure Speech connection. Select the resource again. Only public Azure custom Speech endpoints are supported.");
    }

    public async Task<SpeechSubscription[]> SubscriptionsAsync(CancellationToken ct)
    {
        await _tokenGate.WaitAsync(ct);
        try { _token = null; _subscription = null; _expiry = default; }
        finally { _tokenGate.Release(); }
        var json = JsonNode.Parse(await _az(["account", "list", "--query",
            "[].{id:id,name:name,state:state,cloudName:cloudName,environmentName:environmentName}"], ct)) as JsonArray
            ?? throw new InvalidDataException("Azure CLI did not return a subscription list.");
        var result = json.Where(n => n is not null && Value(n, "state") == "Enabled" &&
            (Value(n, "cloudName") == "AzureCloud" || Value(n, "cloudName").Length == 0 && Value(n, "environmentName") == "AzureCloud"))
            .Select(n => n ?? throw new InvalidDataException("Invalid Azure subscription."))
            .Select(n => new SpeechSubscription(Value(n, "id"), Value(n, "name"))).ToArray();
        if (result.Any(s => !Guid.TryParse(s.Id, out _))) throw new InvalidDataException("Invalid Azure subscription ID.");
        return result;
    }

    public async Task<SpeechCandidate[]> FindAsync(string subscription, CancellationToken ct)
    {
        if (!Guid.TryParse(subscription, out _)) throw new ArgumentException("Select an Azure subscription.");
        var json = JsonNode.Parse(await _az(["cognitiveservices", "account", "list", "--subscription", subscription,
            "--query", "[].{id:id,name:name,kind:kind,location:location,endpoint:properties.endpoint}"], ct)) as JsonArray
            ?? throw new InvalidDataException("Azure CLI did not return a resource list.");
        var accounts = json.Where(n => n is not null && Value(n, "kind") is "AIServices" or "SpeechServices" or "CognitiveServices").ToArray();
        if (accounts.Length > 100)
            throw new InvalidOperationException("More than 100 Speech-capable accounts were found. Use an exact resource ID in Speech settings to bound discovery.");
        using var limit = new SemaphoreSlim(4);
        return await Task.WhenAll(accounts.Select(async node =>
        {
            await limit.WaitAsync(ct);
            try
            {
                var account = node!;
                var candidate = new SpeechCandidate(Value(account, "id"), Value(account, "name"), Value(account, "location"),
                    Value(account, "endpoint"), [], "");
                try
                {
                    var voices = await VoicesAsync(new(candidate.ResourceId, candidate.Endpoint, candidate.Region, "en-US-AvaMultilingualNeural"), ct);
                    return candidate with { Voices = voices, Message = voices.Length > 0 ? "Voice catalog access verified. Selection also tests synthesis with your Azure sign-in." :
                        "No supported English neural voices are available in this resource." };
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                { return candidate with { Message = "Azure Speech voice discovery timed out. Check this resource's network access and retry." }; }
                catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or InvalidDataException or ArgumentException)
                { return candidate with { Message = ex.Message }; }
            }
            finally { limit.Release(); }
        }));
    }

    public async Task<SpeechConnection> SelectAsync(string resourceId, string voice, CancellationToken ct, bool testSynthesis = false)
    {
        var id = Resource(resourceId);
        var account = JsonNode.Parse(await _az(["cognitiveservices", "account", "show", "--subscription", id.Groups[1].Value,
            "--resource-group", id.Groups[2].Value, "--name", id.Groups[3].Value,
            "--query", "{id:id,kind:kind,location:location,endpoint:properties.endpoint}"], ct))
            ?? throw new InvalidDataException("Azure CLI returned no resource.");
        if (!Value(account, "id").Equals(resourceId, StringComparison.OrdinalIgnoreCase) ||
            Value(account, "kind") is not ("AIServices" or "SpeechServices" or "CognitiveServices"))
            throw new InvalidOperationException("Azure did not confirm the selected Speech-capable account.");
        var connection = new SpeechConnection(Value(account, "id"), Value(account, "endpoint"), Value(account, "location"), voice);
        var voices = await VoicesAsync(connection, ct);
        if (!voices.Contains(voice, StringComparer.Ordinal))
            throw new InvalidOperationException("The selected voice is not available to your identity on this resource. Choose another voice.");
        if (testSynthesis) await SynthesizeAsync(connection, "Your PR video narrator is ready.", ct);
        return connection;
    }

    public async Task<string[]> VoicesAsync(SpeechConnection connection, CancellationToken ct)
    {
        Validate(connection);
        using var request = new HttpRequestMessage(HttpMethod.Get, connection.Endpoint.TrimEnd('/') + "/tts/cognitiveservices/voices/list");
        request.Headers.Authorization = await HeaderAsync(connection, ct);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        Check(response);
        var data = await ReadAsync(response, 4_000_000, ct);
        var voices = JsonNode.Parse(data) as JsonArray ?? throw new InvalidDataException("Speech returned an invalid voice catalog.");
        return voices.Where(n => n is not null && Value(n, "Locale") == "en-US" &&
            Value(n, "VoiceType").Contains("Neural", StringComparison.Ordinal) && Value(n, "Status") == "GA")
            .Select(n => Value(n!, "ShortName")).Where(s => Regex.IsMatch(s, @"\A[a-zA-Z0-9][a-zA-Z0-9:._-]{1,100}\z"))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    public async Task<VideoSpeech> SynthesizeAsync(SpeechConnection connection, string text, CancellationToken ct)
    {
        Validate(connection);
        if (string.IsNullOrWhiteSpace(text) || text.Length > 1000)
            throw new ArgumentException("Speech narration must be a short scene, not an arbitrary document.");
        XNamespace ns = "http://www.w3.org/2001/10/synthesis";
        var ssml = new XElement(ns + "speak", new XAttribute("version", "1.0"), new XAttribute(XNamespace.Xml + "lang", "en-US"),
            new XElement(ns + "voice", new XAttribute("name", connection.Voice), text));
        using var request = new HttpRequestMessage(HttpMethod.Post, connection.Endpoint.TrimEnd('/') + "/tts/cognitiveservices/v1")
        { Content = new StringContent(ssml.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "application/ssml+xml") };
        request.Headers.Authorization = await HeaderAsync(connection, ct);
        request.Headers.Add("X-Microsoft-OutputFormat", "riff-24khz-16bit-mono-pcm");
        request.Headers.UserAgent.ParseAdd("AdoPrView/0.1");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        Check(response);
        return ReadWave(await ReadAsync(response, 3_000_000, ct));
    }

    static void Check(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                "Check az login, resource-scoped Cognitive Services Speech User RBAC, and network access. Project access alone is insufficient.",
            HttpStatusCode.TooManyRequests => "Speech quota or rate limit was reached. Check the selected resource's quota and retry later.",
            _ => "Check the selected resource, voice availability, region and Azure Speech service status.",
        };
        throw new HttpRequestException($"Azure Speech returned HTTP {(int)response.StatusCode}. {detail}", null, response.StatusCode);
    }

    async Task<AuthenticationHeaderValue> HeaderAsync(SpeechConnection connection, CancellationToken ct)
    {
        var subscription = Resource(connection.ResourceId).Groups[1].Value;
        await _tokenGate.WaitAsync(ct);
        try
        {
            if (_token is null || _subscription != subscription || DateTimeOffset.UtcNow >= _expiry)
            {
                var json = JsonNode.Parse(await _az(["account", "get-access-token", "--subscription", subscription,
                    "--resource", "https://cognitiveservices.azure.com/"], ct))
                    ?? throw new InvalidDataException("Azure CLI returned no Speech token.");
                _token = Value(json, "accessToken");
                if (_token.Length < 20) throw new InvalidDataException("Azure CLI returned an invalid Speech token. Run az login.");
                if (!long.TryParse(json["expires_on"]?.ToString(), out var expiry))
                    throw new InvalidDataException("Azure CLI did not return token expiry. Update Azure CLI, then sign in again.");
                _expiry = DateTimeOffset.FromUnixTimeSeconds(expiry).AddMinutes(-5);
                _subscription = subscription;
            }
            return new("Bearer", $"aad#{connection.ResourceId}#{_token}");
        }
        finally { _tokenGate.Release(); }
    }

    async Task<byte[]> ReadAsync(HttpResponseMessage response, int maximum, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("Azure Speech response exceeds the size limit.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var maximumWait = TimeSpan.FromMinutes(2);
        timeout.CancelAfter(_http.Timeout > TimeSpan.Zero && _http.Timeout < maximumWait ? _http.Timeout : maximumWait);
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0)
            {
                if (output.Length + read > maximum) throw new InvalidDataException("Azure Speech response exceeds the size limit.");
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        catch (OperationCanceledException ex) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new HttpRequestException("Azure Speech response body timed out. Check network access and retry.", ex);
        }
    }

    internal static VideoSpeech ReadWave(byte[] bytes)
    {
        using var reader = new BinaryReader(new MemoryStream(bytes));
        if (bytes.Length < 44 || reader.ReadUInt32() != 0x46464952) throw new InvalidDataException("Speech returned no RIFF audio.");
        var size = reader.ReadUInt32();
        if (size != bytes.Length - 8 || reader.ReadUInt32() != 0x45564157) throw new InvalidDataException("Speech returned invalid WAV audio.");
        var validFormat = false;
        float[]? samples = null;
        while (reader.BaseStream.Position + 8 <= bytes.Length)
        {
            var chunk = reader.ReadUInt32(); var length = reader.ReadUInt32();
            var next = reader.BaseStream.Position + length + (length % 2);
            if (next > bytes.Length) throw new InvalidDataException("Speech returned truncated audio.");
            if (chunk == 0x20746d66)
                validFormat = length >= 16 && reader.ReadUInt16() == 1 && reader.ReadUInt16() == 1 &&
                    reader.ReadInt32() == 24000 && reader.ReadInt32() == 48000 && reader.ReadUInt16() == 2 && reader.ReadUInt16() == 16;
            if (chunk == 0x61746164)
            {
                if (!validFormat || length < 480 || length % 2 != 0) throw new InvalidDataException("Speech returned unsupported PCM audio.");
                samples = new float[length / 2];
                for (var i = 0; i < samples.Length; i++) samples[i] = reader.ReadInt16() / 32768f;
            }
            reader.BaseStream.Position = next;
        }
        return samples is not null ? new(samples, 24000) : throw new InvalidDataException("Speech returned no audio samples.");
    }

    internal static async Task<string> RunAzAsync(string[] args, CancellationToken ct)
    {
        // The Windows az entry point is normally az.cmd. Arguments are host-owned commands and validated IDs.
        if (args.Any(a => a.Any(c => c is '"' or '\r' or '\n' or '%' or '!' or '&' or '|' or '<' or '>' or '^')))
            throw new ArgumentException("Invalid Azure CLI argument.");
        var command = "az " + string.Join(" ", args.Concat(["--output", "json", "--only-show-errors"]).Select(a => "\"" + a + "\""));
        var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", "/d /s /c \"" + command + "\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        using var process = Process.Start(info) ?? throw new IOException("Could not start Azure CLI. Install az and run az login.");
        using var cancel = timeout.Token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { } // The owned process exited between the check and cancellation.
        });
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(process.ExitCode == 9009
                ? "Azure CLI (az) is not available. Install Azure CLI, restart the app, and run az login to enable TikTok generation."
                : "Azure CLI could not read Azure resources or obtain Speech access. Run az login with the correct tenant and check resource Reader/Speech User permissions. " +
                    (error.Contains("az login", StringComparison.OrdinalIgnoreCase) ? "Azure reports that sign-in is required." : $"CLI exit code: {process.ExitCode}."));
        if (output.Length > 8_000_000) throw new InvalidDataException("Azure CLI returned too much data. Choose a narrower subscription.");
        return output;
    }

    public void Dispose() { _http.Dispose(); _tokenGate.Dispose(); _token = null; }
}

internal sealed class AzureSpeechVoice(AzureSpeech service, Func<SpeechConnection?> connection) : IVideoVoice
{
    SpeechConnection? _current;
    public string Name => "Azure AI Speech / " + (_current?.Voice ?? connection()?.Voice ?? "not configured");
    public async Task PrepareAsync(CancellationToken ct)
    {
        var selected = connection() ?? throw new InvalidOperationException("Choose an Azure Speech resource before generating a video.");
        _current = await service.SelectAsync(selected.ResourceId, selected.Voice, ct);
    }
    public Task<VideoSpeech> SpeakAsync(string text, CancellationToken ct) =>
        service.SynthesizeAsync(_current ?? throw new InvalidOperationException("Azure Speech was not prepared."), text, ct);
}
