using Replica.Core.Diffing;
using Replica.Core.Planning;
using Replica.Core.Snapshots;

namespace Replica.Core.Plugins;

public interface IBuiltInPlugin
{
    string Id { get; }

    string DisplayName { get; }

    string Version { get; }

    Task<PluginDetectionResult> DetectAsync(
        PluginCaptureContext context,
        CancellationToken cancellationToken);

    Task<PluginSnapshot> CaptureAsync(
        PluginCaptureContext context,
        CancellationToken cancellationToken);

    Task<PluginComparisonResult> CompareAsync(
        PluginSnapshot source,
        PluginSnapshot target,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PluginRestoreAction>> BuildRestoreActionsAsync(
        PluginComparisonResult comparison,
        CancellationToken cancellationToken);

    Task<PluginValidationResult> ValidateAsync(
        PluginSnapshot expected,
        PluginCaptureContext context,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PluginSensitiveExclusion>> GetSensitiveExclusionsAsync(
        PluginCaptureContext context,
        CancellationToken cancellationToken);
}

public enum DeveloperKnownPath
{
    UserProfile,
    RoamingApplicationData,
    LocalApplicationData,
    Documents,
}

public enum DeveloperToolQuery
{
    VisualStudioCodeVersion,
    VisualStudioCodeExtensions,
    GitVersion,
    GitConfiguration,
    PowerShellVersion,
    PowerShellModules,
    NodeVersion,
    NpmVersion,
    NpmGlobalPackages,
    PythonInterpreters,
    PythonPackages,
}

public enum BuiltInApplication
{
    PowerToys,
    Everything,
    ObsStudio,
    Minecraft,
    DockerDesktop,
    AbletonLive,
}

public sealed record BuiltInApplicationInfo(
    bool IsInstalled,
    string? Version,
    bool IsRunning,
    string? ExecutablePath = null);

public interface IDeveloperPluginHost
{
    string GetKnownPath(DeveloperKnownPath path);

    bool FileExists(string path);

    bool DirectoryExists(string path);

    long? GetFileSize(string path);

    Task<string?> ReadTextFileAsync(string path, CancellationToken cancellationToken);

    IReadOnlyList<string> EnumerateFiles(string path, string searchPattern, bool recursive);

    BuiltInApplicationInfo GetApplicationInfo(BuiltInApplication application);

    Task<DeveloperToolQueryResult> QueryAsync(
        DeveloperToolQuery query,
        CancellationToken cancellationToken);
}

public sealed record PluginCaptureContext(
    IDeveloperPluginHost Host,
    bool IncludeSshPublicKeyFingerprints = false,
    IReadOnlyList<string>? SelectedRecoveryPaths = null);

public sealed record DeveloperToolQueryResult(
    bool IsAvailable,
    string StandardOutput,
    string? Version = null,
    IReadOnlyList<string>? Warnings = null);

public sealed record PluginDetectionResult(
    bool IsDetected,
    string? DetectedVersion,
    IReadOnlyList<string> Warnings);

public sealed record PluginCapturedFile(
    string LogicalPath,
    string Content,
    string ContentType = "text/plain");

public sealed record PluginSensitiveExclusion(
    string LogicalPath,
    string ReasonCode,
    string Description);

public sealed record PluginSnapshot(
    string PluginId,
    string PluginVersion,
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyList<PluginCapturedFile> Files,
    IReadOnlyList<PluginSensitiveExclusion> Exclusions,
    IReadOnlyList<string> Warnings)
{
    public ReplicaPluginSnapshot ToReplicaSnapshot()
    {
        ValidateForSnapshot();
        return new ReplicaPluginSnapshot(
            PluginId,
            PluginVersion,
            ["Detect", "Capture", "Compare", "RestorePlan", "Validate"],
            [],
            Values,
            Files.Select(file => new ReplicaPluginFile(
                file.LogicalPath,
                file.Content,
                file.ContentType)).ToArray(),
            Exclusions.Select(exclusion => new ReplicaExclusion(
                $"plugins/{PluginId}/{exclusion.LogicalPath}",
                exclusion.ReasonCode,
                exclusion.Description)).ToArray());
    }

    private void ValidateForSnapshot()
    {
        if (string.IsNullOrWhiteSpace(PluginId) || PluginId.Length > 128 ||
            PluginId.Any(character => !char.IsLetterOrDigit(character) && character is not ('.' or '-')) ||
            string.IsNullOrWhiteSpace(PluginVersion) || PluginVersion.Length > 64 ||
            Values is null || Files is null || Exclusions is null || Warnings is null ||
            Values.Count > 10_000 || Files.Count > 10_000 || Exclusions.Count > 10_000 ||
            Values.Any(pair =>
                string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 1024 ||
                pair.Key.Any(char.IsControl) || pair.Value is null || pair.Value.Length > 1024 * 1024) ||
            Files.Any(file => file is null || !IsSafeLogicalPath(file.LogicalPath) ||
                file.Content is null || file.Content.Length > 1024 * 1024 ||
                string.IsNullOrWhiteSpace(file.ContentType) || file.ContentType.Length > 128) ||
            Files.Select(file => file.LogicalPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Files.Count ||
            Exclusions.Any(exclusion => exclusion is null ||
                !IsSafeLogicalPath(exclusion.LogicalPath) ||
                string.IsNullOrWhiteSpace(exclusion.ReasonCode) || exclusion.ReasonCode.Length > 128))
        {
            throw new InvalidDataException("The built-in plugin snapshot contains invalid artifact metadata.");
        }
    }

    private static bool IsSafeLogicalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 ||
            path.StartsWith("/", StringComparison.Ordinal) || path.Contains('\\') ||
            path.Contains(':') || Path.IsPathFullyQualified(path) || path.Any(char.IsControl))
        {
            return false;
        }

        string[] segments = path.Split('/');
        return segments.Length <= 32 && segments.All(segment =>
            !string.IsNullOrWhiteSpace(segment) && segment is not ("." or "..") &&
            !segment.EndsWith('.') && !segment.EndsWith(' '));
    }
}

public sealed record PluginDifference(
    string Key,
    string DisplayName,
    string? SourceValue,
    string? TargetValue,
    DiffType Type,
    bool CanRestoreAutomatically,
    DiffRiskLevel Risk,
    string ReasonCode);

public sealed record PluginComparisonResult(
    string PluginId,
    IReadOnlyList<PluginDifference> Differences,
    IReadOnlyList<string>? Warnings = null);

public sealed record PluginRestoreAction(
    string Id,
    RestoreActionType Type,
    string Name,
    string Description,
    string? TargetValue,
    DiffRiskLevel Risk,
    bool IsManualOnly,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<PluginRestoreStrategy> SupportedStrategies);

public enum PluginRestoreStrategy
{
    Install,
    Merge,
    Replace,
    KeepCurrent,
    Validate,
}

public sealed record PluginValidationResult(
    bool IsValid,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);
