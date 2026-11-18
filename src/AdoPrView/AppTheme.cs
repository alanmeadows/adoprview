using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Wpf;

namespace AdoPrView;

internal sealed record NativePalette(string Canvas, string Subtle, string Surface, string Control, string Hover,
    string Active, string Text, string Muted, string Border, string Focus);

internal static class AppTheme
{
    public static bool IsSupported(string? theme) => theme is "light" or "dark";
    public static string Effective(string? theme) => IsSupported(theme) ? theme! : "light";

    // This small chrome subset mirrors themes.css; the native theme tests check parity.
    public static NativePalette Palette(string? theme) => Effective(theme) switch
    {
        "dark" => new("#0e1014", "#12151b", "#161a22", "#1c212b", "#2b3444", "#343e50", "#e7e9ef", "#a0aabe", "#707e96", "#a6acff"),
        _ => new("#ffffff", "#ffffff", "#ffffff", "#f6f8fa", "#eff2f5", "#e6eaef", "#1f2328", "#59636e", "#818b98", "#0969da"),
    };

    public static string DashboardUri(string? theme, bool mock) =>
        $"https://app.local/index.html?theme={Effective(theme)}{(mock ? "&mock=1" : "")}";

    public static Color Color(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    public static void ApplyResources(FrameworkElement scope, string? theme)
    {
        var p = Palette(theme);
        foreach (var (key, hex) in new Dictionary<string, string>
        {
            ["Canvas"] = p.Canvas, ["Subtle"] = p.Subtle, ["Surface"] = p.Surface,
            ["Control"] = p.Control, ["Hover"] = p.Hover, ["Active"] = p.Active,
            ["Text"] = p.Text, ["Muted"] = p.Muted, ["Border"] = p.Border, ["Focus"] = p.Focus,
        })
        {
            var brush = new SolidColorBrush(Color(hex));
            brush.Freeze();
            scope.Resources["Theme." + key] = brush;
        }
    }

    public static void ApplyWindow(Window window, WebView2 dashboard, string? theme)
    {
        ApplyResources(window, theme);
        var p = Palette(theme);
        window.Background = (Brush)window.Resources["Theme.Canvas"];
        var canvas = Color(p.Canvas);
        dashboard.DefaultBackgroundColor = System.Drawing.Color.FromArgb(canvas.R, canvas.G, canvas.B);
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var dark = Effective(theme) == "dark" ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        SetColor(35, p.Canvas);
        SetColor(36, p.Text);

        void SetColor(int attribute, string hex)
        {
            var c = Color(hex);
            var value = c.R | (c.G << 8) | (c.B << 16);
            DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));
        }
    }

    // Caption colors are best-effort on Windows versions without these DWM attributes.
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
