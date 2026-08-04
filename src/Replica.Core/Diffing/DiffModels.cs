using Replica.Core.Matching;

namespace Replica.Core.Diffing;

public enum DiffType
{
    ExactMatch,
    Missing,
    Extra,
    VersionMismatch,
    ValueMismatch,
    FileChanged,
    Conflict,
    Unsupported,
    SensitiveExcluded,
    ManualActionRequired,
    Error,
}

public enum DiffArea
{
    Applications,
    StoreApplications,
    EnvironmentVariables,
    Path,
    Fonts,
    WindowsInformation,
    PluginSettings,
    ConfigurationFiles,
    SelectedUserFiles,
    DevelopmentEnvironment,
}

public enum DiffScoreCategory
{
    Applications,
    DevelopmentEnvironment,
    ApplicationSettings,
    EnvironmentAndPath,
    FontsAndOther,
}

public enum DiffRiskLevel
{
    None,
    Low,
    Medium,
    High,
    Critical,
}

public enum DiffRestoreMode
{
    Safe,
    Recommended,
    Exact,
}

public sealed record DiffApplicationEntry(
    ApplicationDescriptor Application,
    bool IsSupported = true,
    bool CanRestoreAutomatically = true,
    DiffRiskLevel Risk = DiffRiskLevel.Medium,
    bool RequiresAdministrator = false,
    bool RequiresRestart = false);

public sealed record DiffValueEntry(
    DiffArea Area,
    string Key,
    string DisplayName,
    string? Value = null,
    string? Hash = null,
    bool IsSupported = true,
    bool IsSensitiveExcluded = false,
    bool IsConflict = false,
    bool IsManualActionRequired = false,
    bool IsError = false,
    bool CanRestoreAutomatically = false,
    DiffRiskLevel Risk = DiffRiskLevel.Low,
    bool RequiresAdministrator = false,
    bool RequiresRestart = false);

public sealed record DiffPathEntry(
    string Value,
    string Scope,
    int Order,
    bool IsSupported = true,
    bool IsSensitiveExcluded = false,
    bool CanRestoreAutomatically = true,
    DiffRiskLevel Risk = DiffRiskLevel.Medium,
    bool RequiresAdministrator = false,
    bool RequiresRestart = false);

public sealed record DiffEnvironmentState(
    IReadOnlyList<DiffApplicationEntry> Applications,
    IReadOnlyList<DiffApplicationEntry> StoreApplications,
    IReadOnlyList<DiffValueEntry> Values,
    IReadOnlyList<DiffPathEntry> PathEntries)
{
    public static DiffEnvironmentState Empty { get; } = new([], [], [], []);
}

public sealed record DiffItem(
    DiffType Type,
    DiffArea Area,
    string Key,
    string DisplayName,
    string? SourceValue,
    string? TargetValue,
    ApplicationMatchConfidence? MatchConfidence,
    bool CanAutomaticallyRestore,
    bool PreserveTarget,
    bool AutomaticRemovalSupported,
    DiffRiskLevel Risk,
    bool RequiresAdministrator,
    bool RequiresRestart,
    int SimilarityPercent,
    string ReasonCode);

public sealed record DiffCategoryScore(
    DiffScoreCategory Category,
    int Weight,
    int? Score,
    int ComparableItemCount,
    int ExcludedItemCount);

public sealed record EnvironmentSimilarityScore(
    int? OverallScore,
    int CoveragePercent,
    int UnsupportedCount,
    int SensitiveExcludedCount,
    int ErrorCount,
    IReadOnlyList<DiffCategoryScore> Categories);

public sealed record EnvironmentDiffResult(
    DiffRestoreMode Mode,
    IReadOnlyList<DiffItem> Items,
    EnvironmentSimilarityScore Similarity);
