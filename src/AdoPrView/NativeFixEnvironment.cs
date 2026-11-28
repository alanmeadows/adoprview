namespace AdoPrView;

internal static class NativeFixEnvironment
{
    static readonly string[] Names = ["SystemRoot", "WINDIR", "PATH", "PATHEXT", "TEMP", "TMP", "USERPROFILE",
        "LOCALAPPDATA", "APPDATA", "PROGRAMFILES", "PROGRAMFILES(X86)", "ProgramData", "ComSpec"];

    internal static bool IsAllowed(string name) => Names.Contains(name, StringComparer.OrdinalIgnoreCase) ||
        name.Equals("COPILOT_TELEMETRY_DISABLED", StringComparison.OrdinalIgnoreCase);

    internal static Dictionary<string, string> Create(IReadOnlyDictionary<string, string>? captured = null)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var source = captured is null ? null : new Dictionary<string, string>(captured, StringComparer.OrdinalIgnoreCase);
        foreach (var name in Names)
        {
            var value = source is null ? Environment.GetEnvironmentVariable(name) : source.GetValueOrDefault(name);
            if (value is not null) environment[name] = value;
        }
        environment["COPILOT_TELEMETRY_DISABLED"] = "true";
        return environment;
    }
}
