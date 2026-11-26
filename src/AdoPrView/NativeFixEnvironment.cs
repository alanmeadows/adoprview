namespace AdoPrView;

internal static class NativeFixEnvironment
{
    internal static Dictionary<string, string> Create()
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "SystemRoot", "WINDIR", "PATH", "PATHEXT", "TEMP", "TMP", "USERPROFILE",
            "LOCALAPPDATA", "APPDATA", "PROGRAMFILES", "PROGRAMFILES(X86)", "ProgramData", "ComSpec" })
            if (Environment.GetEnvironmentVariable(name) is { } value) environment[name] = value;
        environment["COPILOT_TELEMETRY_DISABLED"] = "true";
        return environment;
    }
}
