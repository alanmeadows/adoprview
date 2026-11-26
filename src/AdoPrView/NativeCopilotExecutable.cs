using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AdoPrView;

internal static class NativeCopilotExecutable
{
    internal static string? ResolveNpmShim(string shim, out string? diagnostic)
    {
        diagnostic = null;
        try
        {
            if (!File.Exists(shim)) return null;
            NativeFixSupervisor.SafePath(shim);
            if (new FileInfo(shim).Length > 32 * 1024) throw new InvalidDataException();
            var script = File.ReadAllText(shim).Replace('/', '\\');
            if (!script.Contains(@"node_modules\@github\copilot\npm-loader.js", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException();
            var modules = Path.Combine(Path.GetDirectoryName(shim)!, "node_modules");
            var root = Path.Combine(modules, "@github", "copilot");
            using var loader = Manifest(Path.Combine(root, "package.json"));
            var metadata = loader.RootElement;
            if (metadata.GetProperty("name").GetString() != "@github/copilot" ||
                metadata.GetProperty("bin").GetProperty("copilot").GetString() != "npm-loader.js")
                throw new InvalidDataException();
            var version = metadata.GetProperty("version").GetString();
            if (string.IsNullOrWhiteSpace(version)) throw new InvalidDataException();
            var architectures = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? new[] { "arm64", "x64" } : ["x64"];
            foreach (var arch in architectures)
            {
                var packageName = "@github/copilot-win32-" + arch;
                if (!metadata.GetProperty("optionalDependencies").TryGetProperty(packageName, out var dependency) ||
                    dependency.GetString() != version) continue;
                foreach (var parent in new[] { modules, Path.Combine(root, "node_modules") })
                {
                    var platform = Path.Combine(parent, "@github", "copilot-win32-" + arch);
                    var manifest = Path.Combine(platform, "package.json");
                    if (!File.Exists(manifest)) continue;
                    using var native = Manifest(manifest);
                    var package = native.RootElement;
                    if (package.GetProperty("name").GetString() != packageName ||
                        package.GetProperty("version").GetString() != version ||
                        !package.GetProperty("os").EnumerateArray().Any(value => value.GetString() == "win32") ||
                        !package.GetProperty("cpu").EnumerateArray().Any(value => value.GetString() == arch))
                        throw new InvalidDataException();
                    string? entry = null;
                    if (package.TryGetProperty("exports", out var exports))
                    {
                        if (exports.ValueKind == JsonValueKind.Object && exports.TryGetProperty(".", out var main)) exports = main;
                        if (exports.ValueKind == JsonValueKind.Object)
                        {
                            if (exports.TryGetProperty("import", out var import)) exports = import;
                            else if (exports.TryGetProperty("default", out var fallback)) exports = fallback;
                        }
                        if (exports.ValueKind == JsonValueKind.String) entry = exports.GetString();
                    }
                    else if (package.TryGetProperty("main", out var main)) entry = main.GetString();
                    if (entry is null || Path.IsPathFullyQualified(entry) || !entry.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException();
                    var executable = Path.GetFullPath(Path.Combine(platform, entry));
                    if (!executable.StartsWith(Path.GetFullPath(platform) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException();
                    NativeFixSupervisor.SafePath(executable);
                    if (File.Exists(executable) && new FileInfo(executable).Length != 0) return executable;
                }
            }
            diagnostic = "The Windows Copilot npm shim has no matching native platform executable. Repair the @github/copilot optional Windows package or use the standalone Windows CLI.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or
            KeyNotFoundException or ArgumentException or NotSupportedException)
        {
            diagnostic = "The Copilot npm shim/package layout could not be verified safely. Repair the official Windows npm installation or select the standalone native CLI; no shell shim was executed.";
        }
        return null;
    }

    static JsonDocument Manifest(string path)
    {
        NativeFixSupervisor.SafePath(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > 128 * 1024) throw new InvalidDataException();
        return JsonDocument.Parse(file);
    }
}
