using System.Windows;

namespace AdoPrView;

public sealed class Options
{
    public bool Mock { get; set; }
    public string? ScreenshotPath { get; set; }
    public string? Eval { get; set; }
    public int EvalWaitMs { get; set; } = 1500;
    public int? Width { get; set; }
    public int? Height { get; set; }
    public bool HeadlessCapture => ScreenshotPath != null;

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i].ToLowerInvariant())
            {
                case "--mock": o.Mock = true; break;
                case "--screenshot": o.ScreenshotPath = Next(); break;
                case "--eval": o.Eval = Next(); break;
                case "--eval-wait": o.EvalWaitMs = int.Parse(Next()); break;
                case "--size":
                    var parts = Next().Split('x');
                    o.Width = int.Parse(parts[0]); o.Height = int.Parse(parts[1]);
                    break;
            }
        }
        return o;
    }
}

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var diagnostics = new AppDiagnostics(System.IO.Path.Combine(SettingsStore.Dir, "Diagnostics"), ex =>
        {
            var message = $"ADO PR View could not persist its diagnostic log ({ex.GetType().Name}). " +
                @"Startup/exit evidence may be unavailable. Check access and free space in %LOCALAPPDATA%\AdoPrView\Diagnostics.";
            System.Diagnostics.Trace.TraceError(message);
            Console.Error.WriteLine(message);
            MessageBox.Show(message, "ADO PR View diagnostics", MessageBoxButton.OK, MessageBoxImage.Warning);
        });
        diagnostics.RegisterProcessHandlers();
        var opts = Options.Parse(args);
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
            diagnostics.Record("dispatcher-exception-handled", exception: e.Exception);
            MessageBox.Show(e.Exception.Message, "ADO PR View", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
        app.SessionEnding += (_, e) => diagnostics.Record("windows-session-ending", e.ReasonSessionEnding.ToString());
        app.Exit += (_, e) => diagnostics.Record("application-exit", "code-" + e.ApplicationExitCode);
        MainWindow win;
        try { win = new MainWindow(opts, diagnostics); }
        catch (SettingsLoadException ex)
        {
            diagnostics.Record("startup-aborted", "settings-load-failed", ex);
            MessageBox.Show(ex.Message, "ADO PR View settings", MessageBoxButton.OK, MessageBoxImage.Error);
            diagnostics.Complete("settings-load-failed");
            return;
        }
        win.Show();
        diagnostics.Record("window-shown");
        app.Run();
        diagnostics.Complete("application-run-returned");
    }
}
