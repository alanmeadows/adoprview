using System.Net;
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

    static AzureSpeech Service(List<string[]> calls, Func<HttpRequestMessage, HttpResponseMessage> response) =>
        new(new HttpClient(new Transport(r => Task.FromResult(response(r)))), (args, _) => Az(calls, args));
    static Task<string> Az(List<string[]> calls, string[] args)
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
            _ when args.Contains("show") => new { id = Resource, kind = "AIServices", location = "eastus", endpoint = Endpoint },
            _ => new[] {
                new { id = Resource, name = "offline-speech", kind = "AIServices", location = "eastus", endpoint = Endpoint },
                new { id = Resource.Replace("offline-speech", "denied"), name = "denied", kind = "SpeechServices", location = "eastus", endpoint = Endpoint },
                new { id = Resource, name = "not-speech", kind = "OpenAI", location = "eastus", endpoint = Endpoint } },
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
