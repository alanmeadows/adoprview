using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace AdoPrView;

internal sealed record VideoArtifact(string JobId, string Path, string Url);
internal sealed record VideoMediaResponse(int Status, string Reason, string Headers, Stream? Body) : IDisposable
{
    public void Dispose() => Body?.Dispose();
}

internal sealed class VideoArtifacts
{
    readonly string _root;
    readonly Dictionary<string, VideoArtifact> _completed = new(StringComparer.Ordinal);
    readonly object _gate = new();
    public VideoArtifacts(string? root = null) =>
        _root = System.IO.Path.GetFullPath(root ?? System.IO.Path.Combine(SettingsStore.Dir, "Videos"));

    internal static string Id(string id) => Guid.TryParseExact(id, "N", out _) ? id :
        throw new ArgumentException("Invalid video job ID.");
    public string Work(string id) => System.IO.Path.Combine(_root, "jobs", Id(id));
    public string Pending(string id) => System.IO.Path.Combine(Work(id), "render.partial.mp4");
    internal string Ready(string id) => System.IO.Path.Combine(_root, "completed", Id(id) + ".mp4");

    internal static void RequireOrdinaryPath(string path)
    {
        for (string? p = System.IO.Path.GetFullPath(path); p is not null; p = System.IO.Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Video paths cannot contain symbolic links or directory junctions.");
    }

    public void CreateWork(string id)
    {
        RequireOrdinaryPath(Work(id));
        Directory.CreateDirectory(Work(id));
        RequireOrdinaryPath(Work(id));
    }

    public VideoArtifact Publish(string id)
    {
        var pending = Pending(id);
        var ready = Ready(id);
        RequireOrdinaryPath(pending);
        RequireOrdinaryPath(ready);
        if (!File.Exists(pending) || new FileInfo(pending).Length < 1024)
            throw new InvalidDataException("The renderer did not produce a complete MP4.");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ready)!);
        File.Move(pending, ready); // Same-volume rename: partial output is never a playable URL.
        var artifact = new VideoArtifact(id, ready, $"https://pr-video.local/{id}.mp4");
        lock (_gate) _completed.Add(id, artifact);
        return artifact;
    }

    // Only host-registered completed MP4s are addressable. There is no path, URL or directory parameter.
    public VideoMediaResponse Open(string url, string method, string? range)
    {
        const string common = "Content-Type: video/mp4\r\nAccept-Ranges: bytes\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nAccess-Control-Allow-Origin: https://app.local\r\nAccess-Control-Expose-Headers: Content-Range, Accept-Ranges\r\n";
        static VideoMediaResponse Error(int code, string reason, string headers = "") => new(code, reason, common + headers, null);
        if (method is not ("GET" or "HEAD")) return Error(405, "Method Not Allowed", "Allow: GET, HEAD\r\n");
        var match = Regex.Match(url, @"\Ahttps://pr-video\.local/([a-f0-9]{32})\.mp4\z", RegexOptions.CultureInvariant);
        if (!match.Success) return Error(404, "Not Found");
        VideoArtifact? artifact;
        lock (_gate) _completed.TryGetValue(match.Groups[1].Value, out artifact);
        if (artifact is null) return Error(404, "Not Found");
        RequireOrdinaryPath(artifact.Path);
        var file = new FileStream(artifact.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            long start = 0, end = file.Length - 1;
            if (range is not null)
            {
                var bytes = Regex.Match(range, @"\Abytes=(\d*)-(\d*)\z", RegexOptions.CultureInvariant);
                if (!bytes.Success || bytes.Groups[1].Length == 0 && bytes.Groups[2].Length == 0)
                    return Error(416, "Range Not Satisfiable", $"Content-Range: bytes */{file.Length}\r\n");
                if (bytes.Groups[1].Length == 0)
                {
                    if (!long.TryParse(bytes.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var suffix) || suffix <= 0)
                        return Error(416, "Range Not Satisfiable", $"Content-Range: bytes */{file.Length}\r\n");
                    start = Math.Max(0, file.Length - suffix);
                }
                else if (!long.TryParse(bytes.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out start) ||
                    bytes.Groups[2].Length > 0 && !long.TryParse(bytes.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out end))
                    return Error(416, "Range Not Satisfiable", $"Content-Range: bytes */{file.Length}\r\n");
                if (start >= file.Length || start > end)
                    return Error(416, "Range Not Satisfiable", $"Content-Range: bytes */{file.Length}\r\n");
                end = Math.Min(end, file.Length - 1);
            }
            var length = end - start + 1;
            var headers = common + $"Content-Length: {length}\r\n" +
                (range is null ? "" : $"Content-Range: bytes {start}-{end}/{file.Length}\r\n");
            if (method == "HEAD") return new(range is null ? 200 : 206, range is null ? "OK" : "Partial Content", headers, null);
            file.Position = start;
            var result = new VideoMediaResponse(range is null ? 200 : 206, range is null ? "OK" : "Partial Content",
                headers, new SliceStream(file, length));
            file = null!;
            return result;
        }
        finally { file?.Dispose(); }
    }

    sealed class SliceStream(FileStream inner, long length) : Stream
    {
        readonly long _length = length;
        long _remaining = length;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _length - _remaining; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
            _remaining -= read;
            return read;
        }
        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer[..(int)Math.Min(buffer.Length, _remaining)]);
            _remaining -= read;
            return read;
        }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
