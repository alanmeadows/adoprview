using System.Diagnostics;
using System.Text.Json;
using AdoPrView;

static class AppDiagnosticsTests
{
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal static void CreateUnobservedTask() => _ = Task.FromException(new InvalidOperationException("SECRET-UNOBSERVED"));

    static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    static string[] Events(AppDiagnostics log)
    {
        using var file = new FileStream(Path.Combine(log.DirectoryPath, "events.jsonl"), FileMode.Open,
            FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(file);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }
    static bool HasEvent(AppDiagnostics log, string name) =>
        Events(log).Any(line => JsonDocument.Parse(line).RootElement.GetProperty("event").GetString() == name);

    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("Diagnostics flush lifecycle identity and safe exception structure without sensitive messages", () => WithRoot(root =>
        {
            using var log = new AppDiagnostics(root, ex => throw new Exception("Unexpected logging failure", ex));
            Exception error;
            try { throw new InvalidOperationException("SECRET-PAT https://private/pr/123", new IOException("PRIVATE-CODE")); }
            catch (Exception ex) { error = ex; }
            error.Data["auth"] = "SECRET-DATA";
            log.Record("dispatcher-exception-handled", exception: error);
            log.Record("task-exception-unobserved", exception: error);
            log.Record("shutdown-blocked", "window-close", error);
            var text = string.Join("\n", Events(log));
            Check(!text.Contains("SECRET") && !text.Contains("PRIVATE") && !text.Contains("https://"), "Sensitive exception data leaked");
            using var record = JsonDocument.Parse(Events(log)[1]);
            Check(record.RootElement.GetProperty("session").GetString() == log.SessionId &&
                record.RootElement.GetProperty("pid").GetInt32() == Environment.ProcessId &&
                record.RootElement.GetProperty("build").GetString()?.Length == 32 &&
                record.RootElement.GetProperty("utc").GetDateTimeOffset() != default, "Lifecycle identity missing");
            Check(text.Contains("System.InvalidOperationException") && text.Contains("System.IO.IOException") &&
                record.RootElement.GetProperty("exceptions")[0].GetProperty("frames").GetArrayLength() > 0, "Safe exception structure missing");
            Check(!HasEvent(log, "fatal-unhandled"), "Handled/unobserved exceptions mislabeled fatal");
            log.Complete("application-run-returned");
            Check(HasEvent(log, "session-ended"), "Clean end not flushed");
        }));

        await test("Diagnostics distinguish active concurrent instances, clean ends, abandoned sessions and torn final records", () => WithRoot(root =>
        {
            using var active = new AppDiagnostics(root, _ => { });
            using (var concurrent = new AppDiagnostics(root, _ => { }))
            {
                Check(!HasEvent(concurrent, "prior-session-without-clean-end"), "Live instance mislabeled unclean");
                concurrent.Complete("tray-exit");
            }
            string abandoned;
            using (var log = new AppDiagnostics(root, _ => { })) { abandoned = log.DirectoryPath; }
            File.AppendAllText(Path.Combine(abandoned, "events.jsonl"), "{\"torn\":");
            using var next = new AppDiagnostics(root, _ => { });
            var previous = Events(next).Select(line => JsonDocument.Parse(line).RootElement.Clone())
                .Where(e => e.GetProperty("event").GetString() == "prior-session-without-clean-end").ToArray();
            Check(previous.Length == 1 && previous[0].GetProperty("reason").GetString() == Path.GetFileName(abandoned),
                "Unclean evidence missing or clean/live session falsely flagged");
            next.Complete("window-close");
            active.Complete("window-close");
        }));

        await test("Diagnostics tolerate corrupt final record shapes and incomplete sibling initialization", () => WithRoot(root =>
        {
            foreach (var shape in new[] { "[]", "\"text\"", "{\"event\":42}", "{\"event\":null}" })
            {
                string path;
                using (var prior = new AppDiagnostics(root, _ => { })) { path = prior.DirectoryPath; }
                File.WriteAllText(Path.Combine(path, "events.jsonl"), shape);
            }
            Directory.CreateDirectory(Path.Combine(root, "20000101T000000000Z-" + Guid.NewGuid().ToString("N")));
            using var next = new AppDiagnostics(root, _ => { });
            Check(next.Healthy && HasEvent(next, "prior-session-unavailable") &&
                Events(next).Count(line => line.Contains("\"prior-session-without-clean-end\"")) == 4,
                "Incomplete sibling blocked inspection or corrupted log shape escaped into startup");
            next.Complete("window-close");
        }));

        await test("Diagnostics rotate bounded files, retain only recent inactive sessions and preserve live instances", () => WithRoot(root =>
        {
            using var active = new AppDiagnostics(root, _ => { });
            for (var i = 0; i < AppDiagnostics.RetainedSessions + 3; i++)
            {
                using var log = new AppDiagnostics(root, ex => throw new Exception("Unexpected logging failure", ex), 2048);
                for (var n = 0; n < 20; n++) Check(log.Record("window-state", "Minimized"), "Rotation write failed");
                Check(new FileInfo(Path.Combine(log.DirectoryPath, "events.jsonl")).Length <= 2048 &&
                    new FileInfo(Path.Combine(log.DirectoryPath, "events.previous.jsonl")).Length <= 2048, "Rotation exceeded bounds");
                log.Complete("tray-exit");
                Check(Directory.Exists(active.DirectoryPath), "Live session pruned");
            }
            Check(Directory.GetDirectories(root).Length <= AppDiagnostics.RetainedSessions + 2, "Inactive history not bounded");
            active.Complete("window-close");
        }));

        await test("Diagnostics write failure is explicit, reported once and cannot become successful completion", () => WithRoot(root =>
        {
            var blocked = Path.Combine(root, "blocked");
            File.WriteAllText(blocked, "fixture");
            var failures = 0;
            using var log = new AppDiagnostics(blocked, _ => failures++);
            Check(!log.Healthy && failures == 1, "Logging failure not surfaced");
            Check(!log.Record("window-shown"), "Unavailable log reported success");
            log.Complete("application-run-returned");
            log.ProcessExit();
            Check(failures == 1, "Repeated failure notifications");
            using var brokenReporter = new AppDiagnostics(blocked, _ => throw new InvalidOperationException("Broken warning surface"));
            Check(!brokenReporter.Healthy && !brokenReporter.Record("window-shown"), "Reporting failure became success");
        }));

        await test("Diagnostics fatal record cannot be followed by a clean process-exit marker", () => WithRoot(root =>
        {
            using var log = new AppDiagnostics(root, _ => { });
            log.Complete("application-run-returned");
            log.Fatal(new InvalidOperationException("SECRET"), "appdomain-terminating");
            log.ProcessExit();
            Check(HasEvent(log, "fatal-unhandled") && HasEvent(log, "process-exit-without-completion") &&
                !HasEvent(log, "process-exit-clean"), "Fatal exit classified as clean");
        }));

        await test("Diagnostics real process-exit hook records clean return versus early exit without crashing installed app", async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "ado-diagnostics-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                foreach (var mode in new[] { "clean", "early-exit", "unobserved" })
                {
                    var directory = Path.Combine(root, mode);
                    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false,
                        RedirectStandardOutput = true, RedirectStandardError = true };
                    start.ArgumentList.Add("--diagnostics-probe");
                    start.ArgumentList.Add(directory);
                    start.ArgumentList.Add(mode);
                    using var child = Process.Start(start)!;
                    var stdout = child.StandardOutput.ReadToEndAsync();
                    var stderr = child.StandardError.ReadToEndAsync();
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                    await Task.WhenAll(stdout, stderr);
                    Check(child.ExitCode == (mode == "early-exit" ? 7 : 0), "Wrong probe exit");
                    var lines = File.ReadAllLines(Path.Combine(Directory.GetDirectories(directory).Single(), "events.jsonl"));
                    var last = JsonDocument.Parse(lines[^1]).RootElement.GetProperty("event").GetString();
                    Check(last == (mode == "early-exit" ? "process-exit-without-completion" : "process-exit-clean"),
                        "ProcessExit hook lost completion state");
                    if (mode == "unobserved")
                    {
                        Check(lines.Any(line => line.Contains("\"task-exception-unobserved\"")) &&
                            lines.All(line => !line.Contains("SECRET-UNOBSERVED") && !line.Contains("\"fatal-unhandled\"")),
                            "Real unobserved-task hook lost evidence, leaked content or mislabeled fatal");
                    }
                }
            }
            finally { Directory.Delete(root, true); }
        });
    }

    static Task WithRoot(Action<string> run)
    {
        var root = Path.Combine(Path.GetTempPath(), "ado-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { run(root); }
        finally { Directory.Delete(root, true); }
        return Task.CompletedTask;
    }
}
