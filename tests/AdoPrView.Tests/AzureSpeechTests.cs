using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using AdoPrView;

static class AzureSpeechTests
{
    const string Subscription = "00000000-0000-4000-8000-000000000001";
    const string Resource = "/subscriptions/" + Subscription + "/resourceGroups/offline/providers/Microsoft.CognitiveServices/accounts/offline-speech";
    const string Endpoint = "https://offline-speech.cognitiveservices.azure.com/";
    const string Voice = "en-US-Ava:DragonHDLatestNeural";
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static async Task Throws<T>(Func<Task> run) where T : Exception
    {
        try { await run(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("Video Azure discovery is CLI-scoped filters unsupported resources and reports missing RBAC without keys", async () =>
        {
            var calls = new List<string[]>();
            using var speech = Service(calls, request => request.Headers.Authorization!.Parameter!.Contains("denied", StringComparison.Ordinal)
                ? new(HttpStatusCode.Forbidden) : Json(new[] { new { ShortName = Voice, Locale = "en-US", VoiceType = "Neural", Status = "GA" } }));
            var subscriptions = await speech.SubscriptionsAsync(default);
            Check(subscriptions.Length == 1 && subscriptions[0].Id == Subscription, "Subscription scope was lost");
            var found = await speech.FindAsync(Subscription, default);
            Check(found.Length == 2 && found.Count(c => c.Compatible) == 1 &&
                found.Single(c => !c.Compatible).Message.Contains("Speech User"), "RBAC failure was hidden or accepted");
            Check(calls.All(c => !c.Contains("keys") && !c.Contains("create") && !c.Contains("set")) &&
                calls.Any(c => c.Contains("--subscription") && c.Contains(Subscription)), "Discovery expanded Azure authority");
            await Throws<ArgumentException>(() => speech.FindAsync("bad & command", default));
        });
        await test("Video Azure selection verifies actual synthesis rights before configuration can be saved", async () =>
        {
            var calls = new List<string[]>();
            using var speech = Service(calls, r => r.Method == HttpMethod.Post ? new(HttpStatusCode.Forbidden) :
                Json(new[] { new { ShortName = Voice, Locale = "en-US", VoiceType = "Neural", Status = "GA" } }));
            await Throws<HttpRequestException>(() => speech.SelectAsync(Resource, Voice, default, testSynthesis: true));
            await Throws<InvalidOperationException>(() => speech.SelectAsync(Resource, "unavailable-voice", default));
            await Throws<ArgumentException>(() => speech.SelectAsync(Resource + "/projects/arbitrary", Voice, default));
        });
        await test("Video Azure Unicode resource groups survive discovery and exact selection without changing CLI segments", async () =>
        {
            foreach (var group in new[] { "\u00e9quipe", "\u958b\u767a", "ci-\u0661\u0662", "\u01c5\u02b0", "Team.(A)_1", new string('\u958b', 90) })
            {
                var resource = Resource.Replace("/resourceGroups/offline/", $"/resourceGroups/{group}/");
                var calls = new List<string[]>();
                using var speech = Service(calls, _ => Json(new[] { new { ShortName = Voice, Locale = "en-US", VoiceType = "Neural", Status = "GA" } }), resource);
                var found = await speech.FindAsync(Subscription, default);
                Check(found.Single(c => c.ResourceId == resource).Compatible, "Discovery rejected a supported Unicode resource group.");
                var selected = await speech.SelectAsync(resource, Voice, default);
                var show = calls.Single(c => c.Contains("show"));
                Check(selected.ResourceId == resource && show[Array.IndexOf(show, "--resource-group") + 1] == group &&
                    show[Array.IndexOf(show, "--name") + 1] == "offline-speech", "Exact selection changed the resource group or account segment.");
            }
        });
        await test("Video Azure resource IDs reject invalid group categories separators and account names", async () =>
        {
            foreach (var group in new[] { "", new string('a', 91), "trailing.", "a/b", @"a\b", "a%2fb", "a&b", "a|b",
                "a^b", "a!b", "a\"b", "a\nb", "a b", "a?b", "a#b", "a\u00b2", "a\u2167", "e\u0301quipe" })
                await Throws<ArgumentException>(() => Task.FromResult(AzureSpeech.Resource(Resource.Replace("/resourceGroups/offline/", $"/resourceGroups/{group}/"))));
            foreach (var account in new[] { "ab", "Az-12", new string('a', 64) })
                Check(AzureSpeech.Resource(Resource.Replace("accounts/offline-speech", "accounts/" + account)).Success, "A valid ASCII account name was rejected.");
            foreach (var account in new[] { "a", new string('a', 65), "-az", "az-", "az_name", "\u958b\u767a", "\u212a1", "az/child" })
                await Throws<ArgumentException>(() => Task.FromResult(AzureSpeech.Resource(Resource.Replace("accounts/offline-speech", "accounts/" + account))));
            foreach (var suffix in new[] { "/projects/child", "?query=x", "#fragment", "\n" })
                await Throws<ArgumentException>(() => Task.FromResult(AzureSpeech.Resource(Resource + suffix)));
        });
        await test("Video Azure native HTTP transport sends Unicode resource IDs in the authorization header", async () =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var handler = AzureSpeech.CreateHandler();
            handler.UseProxy = false;
            using var http = new HttpClient(handler);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
            var authorization = $"aad#{Resource.Replace("/resourceGroups/offline/", "/resourceGroups/\u958b\u767a/")}#offline-token-never-print";
            request.Headers.Authorization = new("Bearer", authorization);
            var send = http.SendAsync(request, timeout.Token);
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            using var reader = new StreamReader(client.GetStream(), Encoding.UTF8, leaveOpen: true);
            var headers = new List<string>();
            string? line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(timeout.Token))) headers.Add(line);
            await client.GetStream().WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), timeout.Token);
            using var response = await send;
            Check(headers.Contains("Authorization: Bearer " + authorization), "Native HTTP rejected or corrupted the Unicode resource ID.");
        });
        await test("Video Azure narration XML-escapes text uses bounded PCM and never sends tokens or SSML to the model", async () =>
        {
            var calls = new List<string[]>();
            string? body = null;
            using var http = new HttpClient(new Transport(async request =>
            {
                body = await request.Content!.ReadAsStringAsync();
                Check(request.RequestUri!.ToString() == Endpoint + "tts/cognitiveservices/v1" &&
                    request.Headers.GetValues("X-Microsoft-OutputFormat").Single() == "riff-24khz-16bit-mono-pcm" &&
                    request.Headers.Authorization?.Parameter == "aad#" + Resource + "#offline-token-never-print", "Wrong Speech boundary");
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Wave()) };
            }));
            using var speech = new AzureSpeech(http, (args, _) => Az(calls, args));
            const string text = "Before <voice name=\"evil\"> & after.";
            var audio = await speech.SynthesizeAsync(new(Resource, Endpoint, "eastus", Voice), text, default);
            XNamespace ns = "http://www.w3.org/2001/10/synthesis";
            var xml = XDocument.Parse(body!);
            Check(xml.Descendants(ns + "voice").Count() == 1 && xml.Descendants(ns + "voice").Single().Value == text &&
                audio.SampleRate == 24000 && audio.Samples.Length == 24000, "Narration became executable SSML or invalid PCM");
            await Throws<InvalidDataException>(() => speech.SynthesizeAsync(new(Resource, "https://evil.invalid/", "eastus", Voice), "sample", default));
            await Throws<ArgumentException>(() => speech.SynthesizeAsync(new(Resource, Endpoint, "eastus", Voice), new string('x', 1001), default));
        });
        await test("Video Azure stalled response bodies time out without turning caller cancellation into a network failure", async () =>
        {
            foreach (var narration in new[] { false, true })
            {
                foreach (var cancelCaller in new[] { false, true })
                {
                    using var body = new StalledBody();
                    using var http = new HttpClient(new Transport(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                        { Content = new StreamContent(body) }))) { Timeout = TimeSpan.FromMilliseconds(cancelCaller ? 2000 : 100) };
                    using var speech = new AzureSpeech(http, (args, _) => Az([], args));
                    using var cancel = new CancellationTokenSource();
                    Task read = narration ? speech.SynthesizeAsync(new(Resource, Endpoint, "eastus", Voice), "sample", cancel.Token)
                        : speech.VoicesAsync(new(Resource, Endpoint, "eastus", Voice), cancel.Token);
                    await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    if (cancelCaller)
                    {
                        cancel.Cancel();
                        await Throws<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(5)));
                    }
                    else
                    {
                        try
                        {
                            await read.WaitAsync(TimeSpan.FromSeconds(5));
                            throw new Exception("Stalled response body did not time out.");
                        }
                        catch (HttpRequestException ex)
                        {
                            Check(ex.Message.Contains("body timed out") && !cancel.IsCancellationRequested,
                                "Body deadline was not distinguished from caller cancellation.");
                        }
                    }
                }
            }
        });
        await test("Video Azure response body deadlines retain declared and streaming byte limits", async () =>
        {
            foreach (var chunked in new[] { false, true })
            {
                using HttpContent content = chunked ? new StreamContent(new ChunkedBody(new byte[4_000_001])) : new ByteArrayContent([]);
                if (!chunked) content.Headers.ContentLength = 4_000_001;
                using var http = new HttpClient(new Transport(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content })));
                using var speech = new AzureSpeech(http, (args, _) => Az([], args));
                await Throws<InvalidDataException>(() => speech.VoicesAsync(new(Resource, Endpoint, "eastus", Voice), default));
            }
        });
        await test("Video Azure malformed audio shell arguments and cancelled operations fail explicitly", async () =>
        {
            foreach (var bytes in new[] { Array.Empty<byte>(), Encoding.UTF8.GetBytes("not audio"), Wave()[..80] })
                await Throws<InvalidDataException>(() => Task.Run(() => AzureSpeech.ReadWave(bytes)));
            await Throws<ArgumentException>(() => AzureSpeech.RunAzAsync(["account", "show", "bad&command"], default));
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            using var speech = new AzureSpeech(new HttpClient(new Transport(_ => throw new Exception("Cancelled request escaped"))),
                (_, ct) => Task.FromCanceled<string>(ct));
            await Throws<OperationCanceledException>(() => speech.SubscriptionsAsync(cancel.Token));
        });
    }

    static AzureSpeech Service(List<string[]> calls, Func<HttpRequestMessage, HttpResponseMessage> response, string resource = Resource) =>
        new(new HttpClient(new Transport(r => Task.FromResult(response(r)))), (args, _) => Az(calls, args, resource));
    static Task<string> Az(List<string[]> calls, string[] args, string resource = Resource)
    {
        lock (calls) calls.Add(args);
        object result = args[1] switch
        {
            "get-access-token" => new { accessToken = "offline-token-never-print", expires_on = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() },
            "list" => new object[] {
                new { id = Subscription, name = "Offline subscription", state = "Enabled", cloudName = "AzureCloud", environmentName = (string?)null },
                new { id = Subscription, name = "Sovereign cloud", state = "Enabled", cloudName = "AzureUSGovernment", environmentName = "AzureCloud" },
                new { id = Subscription, name = "Disabled", state = "Disabled", cloudName = "AzureCloud" },
                new { id = Subscription, name = "Unknown cloud", state = "Enabled" } },
            _ when args.Contains("show") => new { id = resource, kind = "AIServices", location = "eastus", endpoint = Endpoint },
            _ => new[] {
                new { id = resource, name = "offline-speech", kind = "AIServices", location = "eastus", endpoint = Endpoint },
                new { id = resource.Replace("offline-speech", "denied"), name = "denied", kind = "SpeechServices", location = "eastus", endpoint = Endpoint },
                new { id = resource, name = "not-speech", kind = "OpenAI", location = "eastus", endpoint = Endpoint } },
        };
        return Task.FromResult(JsonSerializer.Serialize(result));
    }
    static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    static byte[] Wave()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write("RIFF"u8); writer.Write(48036); writer.Write("WAVEfmt "u8); writer.Write(16);
        writer.Write((short)1); writer.Write((short)1); writer.Write(24000); writer.Write(48000);
        writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(48000);
        for (var i = 0; i < 24000; i++) writer.Write((short)3000);
        return stream.ToArray();
    }
    sealed class Transport(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request);
    }
    sealed class ChunkedBody(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
    sealed class StalledBody : Stream
    {
        readonly TaskCompletionSource<int> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { Entered.TrySetResult(); return new(_completion.Task.WaitAsync(ct)); }
        protected override void Dispose(bool disposing) { if (disposing) _completion.TrySetResult(0); base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
