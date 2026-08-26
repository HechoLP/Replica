using Replica.Core.Execution;
using Replica.Core.Planning;
using Replica.Core.Snapshots;

namespace Replica.Core.Recovery;

public enum RecoveryWizardStep
{
    SelectSnapshot = 1,
    ValidateSnapshot = 2,
    EnterPassword = 3,
    ShowSourceComputer = 4,
    ScanCurrentComputer = 5,
    CheckCompatibility = 6,
    AnalyzeApplications = 7,
    AnalyzeSettings = 8,
    ConfirmFileDestinations = 9,
    CreateRestorePlan = 10,
    FinalApproval = 11,
    InstallApplications = 12,
    RestoreSettings = 13,
    RestoreUserFiles = 14,
    ShowRestartRequirements = 15,
    ResumeAfterRestart = 16,
    FinalValidation = 17,
    ShowSimilarity = 18,
    ShowManualActions = 19,
}

public enum RecoveryWizardStatus
{
    InProgress,
    AwaitingApproval,
    Executing,
    AwaitingRestartApproval,
    AwaitingResumeConfirmation,
    Verifying,
    Completed,
    Cancelled,
    Failed,
}

public enum RecoveryPathMappingScope
{
    SelectedFolder,
    ApplicationSetting,
    GameSave,
    Project,
}

public sealed record ReplicaHardwareInfo(
    IReadOnlyList<string> GraphicsAdapters,
    IReadOnlyList<ReplicaDisplayInfo> Displays,
    IReadOnlyList<string> AudioDevices,
    IReadOnlyList<string> DriveRoots);

public sealed record ReplicaDisplayInfo(
    string Name,
    int Width,
    int Height,
    bool IsPrimary);

public sealed record RecoveryHardwareDifference(
    string Category,
    string Source,
    string Target,
    string ReasonCode,
    string Guidance,
    bool BlocksHardwareDependentRestore);

public sealed record RecoveryPathMapping(
    string SourcePath,
    string TargetPath,
    RecoveryPathMappingScope Scope,
    RestoreFileConflictBehavior ConflictBehavior,
    long EstimatedBytes);

public sealed record RecoveryManualAction(
    string Id,
    string DisplayName,
    string Guidance,
    string ReasonCode);

public sealed record RecoveryResultSummary(
    int? SimilarityBefore,
    int? SimilarityAfter,
    int InstallSucceeded,
    int SettingsSucceeded,
    int FilesSucceeded,
    int Failed,
    int Skipped,
    bool RequiresRestart,
    int ManualActionCount);

public sealed record RecoveryPreparedSnapshot(
    Guid SnapshotId,
    SnapshotType SnapshotType,
    string SourceMachineName,
    string WindowsVersion,
    string Architecture,
    string Locale,
    DateTimeOffset CreatedAtUtc,
    bool IsEncrypted,
    IReadOnlyList<RecoveryPathMapping> SuggestedMappings,
    string SnapshotSha256);

public sealed record RecoveryAnalysisResult(
    RestorePlan Plan,
    int? SimilarityBefore,
    IReadOnlyList<RecoveryHardwareDifference> HardwareDifferences,
    IReadOnlyList<RecoveryPathMapping> PathMappings,
    IReadOnlyList<RecoveryManualAction> ManualActions,
    bool HasSufficientStorage,
    long RequiredBytes,
    long AvailableBytes,
    Guid SnapshotId = default,
    string SnapshotSha256 = "");

public sealed record RecoveryExecutionBatch(
    IReadOnlyList<RestoreActionExecutionResult> Actions,
    bool RequiresRestart,
    bool WasCancelled,
    bool PayloadCleanupPending = false);

public sealed record RecoveryVerificationResult(
    int? SimilarityAfter,
    IReadOnlyList<RecoveryManualAction> ManualActions);

public sealed record RecoveryWizardSession(
    string SessionId,
    string SnapshotPath,
    Guid SnapshotId,
    RecoveryWizardStatus Status,
    RecoveryWizardStep CurrentStep,
    string SourceMachineName,
    string SourceWindowsVersion,
    string SourceArchitecture,
    string SourceLocale,
    bool SnapshotEncrypted,
    RestorePlan? Plan,
    IReadOnlyList<RecoveryPathMapping> PathMappings,
    IReadOnlyList<RecoveryHardwareDifference> HardwareDifferences,
    IReadOnlyList<RecoveryManualAction> ManualActions,
    IReadOnlyList<RestoreActionExecutionResult> ActionResults,
    IReadOnlyList<string> CompletedActionIds,
    IReadOnlyList<string> FailedActionIds,
    int? SimilarityBefore,
    int? SimilarityAfter,
    bool RequiresRestart,
    bool RestartApproved,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? FailureReasonCode = null,
    string SnapshotSha256 = "",
    string? ReviewBindingSha256 = null,
    long? RequiredBytes = null,
    long? AvailableBytes = null)
{
    public bool RequiresPassword => SnapshotEncrypted;

    public bool CanRetry => FailedActionIds.Count > 0;
}

public sealed record RecoveryStartResult(
    RecoveryWizardSession? Session,
    bool PasswordRequired);
