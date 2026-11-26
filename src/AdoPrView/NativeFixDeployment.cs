using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AdoPrView;

internal static class NativeFixDeployment
{
    internal sealed record Deployment(string BundleId, string Executable);
    internal sealed class DeploymentException(string message) : InvalidOperationException(message);
    sealed record Item(string Name, string Hash);
    sealed record Manifest(int Version, string BundleId, Item[] Files);

    internal static Deployment Ensure(string appDirectory, string stateDirectory)
    {
        var source = Path.Combine(Path.GetFullPath(appDirectory), "FixSupervisor");
        NativeFixSupervisor.SafePath(source);
        if (!Directory.Exists(source))
            throw new DeploymentException("The separate native Fix supervisor payload is missing. Reinstall the complete application; no worktree or task was launched.");
        var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
            .Where(path => PayloadFile(Path.GetRelativePath(source, path)))
            .Select(path => new Item(Path.GetRelativePath(source, path), Hash(path)))
            .OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
        foreach (var required in new[] { "AdoPrView.FixSupervisor.exe", "AdoPrView.FixSupervisor.dll",
            "AdoPrView.FixSupervisor.deps.json", "AdoPrView.FixSupervisor.runtimeconfig.json" })
            if (!files.Any(item => item.Name == required))
                throw new DeploymentException("The separate native Fix supervisor payload is incomplete. Reinstall the complete application; no worktree or task was launched.");
        using (var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(source, "AdoPrView.FixSupervisor.runtimeconfig.json"))))
            if (config.RootElement.GetProperty("runtimeOptions").TryGetProperty("includedFrameworks", out _))
                foreach (var required in new[] { "coreclr.dll", "hostfxr.dll", "hostpolicy.dll" })
                    if (!files.Any(item => item.Name == required))
                        throw new DeploymentException("The self-contained Fix supervisor runtime is incomplete. No worktree or task was launched.");
        using (var dependencies = JsonDocument.Parse(File.ReadAllText(Path.Combine(source, "AdoPrView.FixSupervisor.deps.json"))))
        {
            var target = dependencies.RootElement.GetProperty("runtimeTarget").GetProperty("name").GetString()!;
            foreach (var package in dependencies.RootElement.GetProperty("targets").GetProperty(target).EnumerateObject())
                foreach (var kind in new[] { "runtime", "native" })
                    if (package.Value.TryGetProperty(kind, out var assets))
                        foreach (var asset in assets.EnumerateObject())
                        {
                            var name = Path.GetFileName(asset.Name.Replace('/', Path.DirectorySeparatorChar));
                            if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                                name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                                if (!files.Any(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                                    throw new DeploymentException("The separate Fix supervisor's declared runtime dependencies are incomplete. Reinstall the complete payload; no worktree or task was launched.");
                        }
        }
        var bundle = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join("\n", files.Select(item => item.Name + "\0" + item.Hash))))).ToLowerInvariant();
        var manifest = new Manifest(1, bundle, files);
        var root = Path.Combine(Path.GetFullPath(stateDirectory), "supervisors");
        var destination = Path.Combine(root, bundle);
        NativeFixSupervisor.SafePath(destination);
        Directory.CreateDirectory(root);
        if (!Directory.Exists(destination))
        {
            var staging = Path.Combine(root, bundle + ".staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                foreach (var item in files)
                {
                    var target = Path.Combine(staging, item.Name);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(Path.Combine(source, item.Name), target);
                    if (Hash(target) != item.Hash)
                        throw new DeploymentException("The supervisor payload changed during deployment. Restart after the application update; no task was launched.");
                }
                NativeFixSupervisor.Write(Path.Combine(staging, "bundle.json"), manifest);
                try { Directory.Move(staging, destination); }
                catch (IOException) when (Directory.Exists(destination)) { }
            }
            finally
            {
                // Only our unpublished staging copy may be removed; completed/active versions are never pruned.
                if (Directory.Exists(staging))
                {
                    foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
                        File.SetAttributes(file, FileAttributes.Normal);
                    Directory.Delete(staging, true);
                }
            }
        }
        var saved = NativeFixSupervisor.Read<Manifest>(Path.Combine(destination, "bundle.json"));
        if (saved.Version != 1 || saved.BundleId != bundle || JsonSerializer.Serialize(saved) != JsonSerializer.Serialize(manifest) ||
            files.Any(item => Hash(Path.Combine(destination, item.Name)) != item.Hash) ||
            !Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(destination, path)).Where(name => name != "bundle.json")
                .OrderBy(name => name, StringComparer.Ordinal).SequenceEqual(files.Select(item => item.Name)))
            throw new DeploymentException("The owned supervisor deployment changed or is corrupt. Existing versions and jobs were preserved; repair the separate supervisor payload before starting new runs.");
        return new(bundle, Path.Combine(destination, "AdoPrView.FixSupervisor.exe"));
    }

    static bool PayloadFile(string name) =>
        !name.Contains(Path.DirectorySeparatorChar) &&
            (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
             name is "AdoPrView.FixSupervisor.deps.json" or "AdoPrView.FixSupervisor.runtimeconfig.json" or
                 "LICENSE.txt" or "THIRD-PARTY-NOTICES.txt") ||
        name.StartsWith("licenses" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            (name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".md", StringComparison.OrdinalIgnoreCase));

    static string Hash(string path)
    {
        NativeFixSupervisor.SafePath(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
    }
}
