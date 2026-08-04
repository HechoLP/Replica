namespace Replica.Core.Scanning;

public enum EnvironmentScanStage
{
    WindowsInformation,
    Applications,
    StoreApplications,
    EnvironmentVariables,
    Path,
    Fonts,
    BuiltInPlugins,
    Completed,
}

public sealed record EnvironmentScanProgress(
    EnvironmentScanStage Stage,
    int CompletedStages,
    int TotalStages,
    string Status);

public sealed record ScanWarning(
    string Provider,
    string Code,
    string Message);

public sealed record WindowsEnvironmentInfo(
    string Edition,
    string Version,
    string Build,
    string Architecture,
    string Locale,
    string TimeZone,
    string MachineName,
    bool IsAdministrator,
    bool IsWinGetAvailable,
    bool IsPowerShellAvailable,
    bool IsWindowsTerminalAvailable);

public sealed record ScannedApplication(
    string Name,
    string? Version,
    string? Publisher,
    string? InstallLocation,
    string? WinGetId,
    string Source,
    string Scope,
    string Architecture,
    string InstallType,
    bool IsRestorable,
    bool IsFramework = false,
    string? PackageFamilyName = null);

public sealed record WinGetPackage(
    string PackageId,
    string? Name,
    string? Version,
    string? Source);

public sealed record ScannedEnvironmentVariable(
    string Name,
    string? Value,
    string Scope,
    bool IsSensitive,
    string? ExclusionReason);

public sealed record ScannedPathEntry(
    string Value,
    string Scope,
    int Order,
    bool IsDuplicate,
    bool Exists);

public sealed record EnvironmentVariableScanResult(
    IReadOnlyList<ScannedEnvironmentVariable> Variables,
    IReadOnlyList<ScannedPathEntry> PathEntries,
    int SensitiveExclusionCount,
    IReadOnlyList<ScanWarning> Warnings);

public sealed record ScannedFont(
    string FamilyName,
    string Style,
    string InstallScope,
    string FilePath,
    bool IsRestorable);

public sealed record ApplicationScanResult(
    IReadOnlyList<ScannedApplication> Applications,
    int WinGetMatchCount,
    IReadOnlyList<ScanWarning> Warnings);

public sealed record EnvironmentScanSummary(
    int ApplicationCount,
    int WinGetMatchCount,
    int UnmatchedApplicationCount,
    int EnvironmentVariableCount,
    int SensitiveExclusionCount,
    int WarningCount);

public sealed record EnvironmentScanResult(
    WindowsEnvironmentInfo? Windows,
    IReadOnlyList<ScannedApplication> Applications,
    IReadOnlyList<ScannedEnvironmentVariable> EnvironmentVariables,
    IReadOnlyList<ScannedPathEntry> PathEntries,
    IReadOnlyList<ScannedFont> Fonts,
    IReadOnlyList<string> BuiltInPluginIds,
    IReadOnlyList<ScanWarning> Warnings,
    EnvironmentScanSummary Summary);

public sealed record WinGetScanResult(
    bool IsAvailable,
    IReadOnlyList<WinGetPackage> Packages,
    IReadOnlyList<ScanWarning> Warnings);

public sealed record RegistryApplicationScanResult(
    IReadOnlyList<ScannedApplication> Applications,
    IReadOnlyList<ScanWarning> Warnings);

public sealed record MsixApplicationScanResult(
    IReadOnlyList<ScannedApplication> Applications,
    IReadOnlyList<ScanWarning> Warnings);

public sealed record FontScanResult(
    IReadOnlyList<ScannedFont> Fonts,
    IReadOnlyList<ScanWarning> Warnings);
