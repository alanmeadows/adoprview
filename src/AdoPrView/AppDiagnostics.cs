using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AdoPrView;

/// Lifecycle records contain host-owned labels and exception structure, never exception messages or data.
internal sealed class AppDiagnostics : IDisposable
{
    internal const int MaxLogBytes = 512 * 1024;
    internal const int RetainedSessions = 10;
    static readonly Regex SessionName = new(@"^\d{8}T\d{9}Z-[0-9a-f]{32}$", RegexOptions.CultureInvariant);
    readonly object gate = new();
    readonly Action<Exception> reportFailure;
    readonly int maxBytes;
    readonly string version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
    readonly string build = typeof(Program).Assembly.ManifestModule.ModuleVersionId.ToString("N");
    FileStream? lease;
    FileStream? output;
    bool failureReported;
    bool disposed;
    bool completed;

    internal string SessionId { get; } = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'") + "-" + Guid.NewGuid().ToString("N");
    internal string DirectoryPath { get; }
    internal bool Healthy { get; private set; } = true;

    internal void RegisterProcessHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.IsTerminating) Fatal(e.ExceptionObject as Exception, "appdomain-terminating");
            else Record("appdomain-unhandled-nonterminating", exception: e.ExceptionObject as Exception);
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ProcessExit();
        TaskScheduler.UnobservedTaskException += (_, e) =>
            Record("task-exception-unobserved", exception: e.Exception); // Preserve observation/runtime policy.
    }

    internal AppDiagnostics(string root, Action<Exception> reportFailure, int maxBytes = MaxLogBytes)
    {
        this.reportFailure = reportFailure;
        this.maxBytes = maxBytes;
        DirectoryPath = Path.Combine(root, SessionId);
        TryIo(() =>
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(DirectoryPath);
            lease = new FileStream(Path.Combine(DirectoryPath, "session.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            output = OpenLog();
        });
        Record("startup");
        TryIo(() => InspectPreviousSessions(root));
    }

    internal bool Record(string kind, string? reason = null, Exception? exception = null)
    {
        lock (gate)
        {
            if (disposed || output is null) return false;
            return TryIo(() =>
            {
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                {
                    utc = DateTimeOffset.UtcNow, session = SessionId, pid = Environment.ProcessId,
                    version, build, @event = kind, reason,
                    exceptions = DescribeException(exception),
                }) + "\n");
                if (output.Length + bytes.Length > maxBytes)
                {
                    output.Dispose();
                    output = null;
                    File.Move(Path.Combine(DirectoryPath, "events.jsonl"), Path.Combine(DirectoryPath, "events.previous.jsonl"), true);
                    output = OpenLog();
                }
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            });
        }
    }

    internal void Complete(string reason)
    {
        lock (gate)
            completed = Record("session-ended", reason);
    }

    internal void Fatal(Exception? exception, string reason)
    {
        lock (gate)
        {
            completed = false;
            Record("fatal-unhandled", reason, exception);
        }
    }

    internal void ProcessExit()
    {
        lock (gate)
        {
            Record(completed ? "process-exit-clean" : "process-exit-without-completion");
            Dispose();
        }
    }

    FileStream OpenLog() => new(Path.Combine(DirectoryPath, "events.jsonl"), FileMode.Create,
        FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);

    void InspectPreviousSessions(string root)
    {
        var inactive = new List<string>();
        foreach (var directory in Directory.GetDirectories(root).OrderDescending(StringComparer.Ordinal))
        {
            if (directory == DirectoryPath || !SessionName.IsMatch(Path.GetFileName(directory)) ||
                File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) continue;
            FileStream previousLease;
            try
            {
                previousLease = new FileStream(Path.Combine(directory, "session.lock"), FileMode.Open,
                    FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33) { continue; } // Another instance is still alive.
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                Record("prior-session-unavailable", Path.GetFileName(directory));
                continue; // An incomplete initialization or concurrent prune cannot establish a prior exit.
            }
            using (previousLease)
            {
                var path = Path.Combine(directory, "events.jsonl");
                // A missing/torn final record means only "no clean completion recorded", not "crashed".
                var last = File.Exists(path) ? File.ReadLines(path).LastOrDefault() : null;
                var clean = false;
                if (last is not null)
                {
                    try
                    {
                        using var json = JsonDocument.Parse(last);
                        clean = json.RootElement.ValueKind == JsonValueKind.Object &&
                            json.RootElement.TryGetProperty("event", out var value) && value.ValueKind == JsonValueKind.String &&
                            value.GetString() is "session-ended" or "process-exit-clean";
                    }
                    catch (JsonException) { }
                }
                if (!clean && inactive.Count < RetainedSessions)
                    Record("prior-session-without-clean-end", Path.GetFileName(directory));
                inactive.Add(directory);
                if (inactive.Count <= RetainedSessions) continue;
                foreach (var name in new[] { "events.jsonl", "events.previous.jsonl" })
                    File.Delete(Path.Combine(directory, name));
            }
            if (inactive.Count > RetainedSessions)
            {
                File.Delete(Path.Combine(directory, "session.lock"));
                Directory.Delete(directory); // Never recursively remove unknown contents.
            }
        }
    }

    static object[] DescribeException(Exception? exception)
    {
        var result = new List<object>();
        for (var depth = 0; exception is not null && depth < 4; depth++, exception = exception.InnerException)
        {
            var frames = new StackTrace(exception, false).GetFrames().Take(24)
                .Select(frame => frame.GetMethod())
                .Where(method => method is not null)
                .Select(method => method!.DeclaringType?.FullName + "." + method.Name).ToArray();
            result.Add(new { type = exception.GetType().FullName, hresult = exception.HResult, frames });
        }
        return result.ToArray();
    }

    bool TryIo(Action action)
    {
        try { action(); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            Healthy = false;
            if (!failureReported)
            {
                failureReported = true;
                try { reportFailure(ex); }
                catch (Exception)
                {
                    // A broken reporting surface must not turn a logging failure into an app failure.
                    try { Console.Error.WriteLine("ADO PR View diagnostic logging and its failure notification are unavailable."); }
                    catch (IOException) { }
                }
            }
            return false;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            TryIo(() => output?.Dispose());
            TryIo(() => lease?.Dispose());
        }
    }
}
