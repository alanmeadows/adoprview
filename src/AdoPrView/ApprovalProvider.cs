using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json.Serialization;
using System.Windows;

namespace AdoPrView;

/// Contract for an optional, locally installed native approval provider. A provider is trusted
/// in-process code, not a sandboxed plugin. The dashboard only renders the data it returns.
public static class ApprovalProviderContract
{
    public const int Version = 1;
}

/// Returned when the provider can act on a PR. Null means the action is unavailable.
public sealed record ApprovalOffer(string Title, string? Detail = null);

public sealed record ApprovalOutcome(string Kind, string Message, string? Response = null, string? RefreshWarning = null)
{
    /// Ask the dashboard to re-read the PR after the request, whatever the outcome.
    [JsonIgnore] public bool RefreshPr { get; init; }
}

public sealed record ApprovalSettingsAction(string Id, string Label);

public sealed record ApprovalSettingsView(string Message, string? Help, IReadOnlyList<ApprovalSettingsAction> Actions);

public sealed record ApprovalProviderInfo(string Name, string? LinkUrl, string? LinkLabel);

public interface IApprovalUi
{
    /// Runs on the dashboard UI thread with the main window as owner for native dialogs.
    Task<T> OnUiThreadAsync<T>(Func<Window?, T> action);
}

/// Host services scoped to the single PR captured for one approval request.
public interface IApprovalHost : IApprovalUi
{
    string Key { get; }
    /// Re-reads the captured PR from Azure DevOps and returns the fresh view.
    Task<PrView> RefreshPrAsync();
    bool IsHidden();
    /// Removes one reviewer from the captured PR only.
    Task RemoveReviewerAsync(PrView pr, string reviewerId);
}

public interface IApprovalProvider
{
    int ContractVersion { get; }
    string Name { get; }
    string? LinkUrl { get; }
    string? LinkLabel { get; }
    ApprovalOffer? Evaluate(PrView pr);
    Task<ApprovalOutcome> RequestAsync(PrView pr, IApprovalHost host);
    ApprovalSettingsView? GetSettings();
    Task<string> InvokeSettingsActionAsync(string actionId, IApprovalUi ui);
}

public static class ApprovalProviderLoader
{
    /// Loads nothing and touches no files unless the settings explicitly enable a provider.
    public static (IApprovalProvider? Provider, string? Error) Load(ApprovalProviderSettings? settings)
    {
        if (settings is not { Enabled: true }) return (null, null);
        const string prefix = "The configured approval provider was not loaded: ";
        var path = settings.AssemblyPath;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return (null, prefix + "its assembly path must be an absolute local path.");
        if (!File.Exists(path)) return (null, prefix + $"{path} does not exist.");
        try
        {
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path));
            var types = assembly.GetExportedTypes()
                .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IApprovalProvider).IsAssignableFrom(t)).ToList();
            if (types.Count != 1)
                return (null, prefix + $"{Path.GetFileName(path)} must export exactly one provider (found {types.Count}).");
            if (Activator.CreateInstance(types[0]) is not IApprovalProvider provider)
                return (null, prefix + "the provider could not be created.");
            if (provider.ContractVersion != ApprovalProviderContract.Version)
                return (null, prefix + $"contract version {provider.ContractVersion} is not supported (expected {ApprovalProviderContract.Version}).");
            if (string.IsNullOrWhiteSpace(provider.Name))
                return (null, prefix + "the provider has no name.");
            if (provider.LinkUrl is { } link && !(Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps))
                return (null, prefix + "the provider link must be an https URL.");
            return (provider, null);
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or ReflectionTypeLoadException
            or TypeLoadException or MissingMethodException or TargetInvocationException or IOException)
        {
            return (null, prefix + ex.Message);
        }
    }
}
