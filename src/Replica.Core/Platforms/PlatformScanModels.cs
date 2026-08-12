using Replica.Core.Snapshots;

namespace Replica.Core.Platforms;

public enum PlatformScanStage
{
    SystemInformation,
    Applications,
    HomebrewPackages,
    EnvironmentVariables,
    Path,
    Fonts,
    Completed,
}

public sealed record PlatformScanProgress(
    PlatformScanStage Stage,
    int CompletedStages,
    int TotalStages,
    string Status);

public sealed record PlatformScanWarning(
    string Provider,
    string Code,
    string Message);

public sealed record PlatformApplication(
    string Name,
    string? Version,
    string? Publisher,
    string? InstallLocation,
    string? BundleIdentifier,
    string? HomebrewPackageId,
    string Source,
    string Architecture,
    bool IsRestorable);

public sealed record PlatformEnvironmentVariable(
    string Name,
    string? Value,
    bool IsSensitive,
    string? ExclusionReason);

public sealed record PlatformPathEntry(
    string Value,
    int Order,
    bool IsDuplicate,
    bool Exists);

public sealed record PlatformFont(
    string FamilyName,
    string Style,
    string InstallScope,
    string FilePath);

public sealed record PlatformScanSummary(
    int ApplicationCount,
    int HomebrewPackageCount,
    int EnvironmentVariableCount,
    int SensitiveExclusionCount,
    int FontCount,
    int WarningCount);

public sealed record PlatformScanResult(
    ReplicaPlatformInfo Platform,
    string MachineName,
    IReadOnlyList<PlatformApplication> Applications,
    IReadOnlyList<PlatformEnvironmentVariable> EnvironmentVariables,
    IReadOnlyList<PlatformPathEntry> PathEntries,
    IReadOnlyList<PlatformFont> Fonts,
    IReadOnlyList<PlatformScanWarning> Warnings,
    PlatformScanSummary Summary);
