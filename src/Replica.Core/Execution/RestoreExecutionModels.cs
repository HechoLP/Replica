using Replica.Core.Matching;
using Replica.Core.Planning;

namespace Replica.Core.Execution;

public enum RestoreExecutionState
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Skipped,
    Cancelled,
    RequiresRestart,
    RolledBack,
}

public enum RestoreFileConflictBehavior
{
    KeepExisting,
    OverwriteWithSnapshot,
    RenameAndKeepBoth,
    KeepNewest,
    PromptForEachConflict,
}

public enum EnvironmentVariableScope
{
    User,
    Machine,
}

public enum RegistryValueDataKind
{
    String,
    ExpandString,
    DWord,
    QWord,
    MultiString,
    Binary,
}

public sealed record RestoreProgress(
    string SessionId,
    string ActionId,
    string ActionName,
    RestoreExecutionState State,
    int CompletedActions,
    int TotalActions,
    string Message);

public sealed record RestoreActionExecutionResult(
    string ActionId,
    RestoreExecutionState State,
    string ReasonCode,
    string Message,
    bool RequiresRestart = false,
    int? ExitCode = null,
    bool OutputTruncated = false,
    string? MutationTargetPath = null);

public sealed record RestoreExecutionResult(
    string SessionId,
    IReadOnlyList<RestoreActionExecutionResult> Actions,
    bool RequiresRestart,
    bool WasCancelled);

public sealed record WinGetInstallRequest(
    string PackageIdentifier,
    string? ExpectedVersion,
    ApplicationMatchConfidence Confidence,
    bool IsUpdate,
    TimeSpan Timeout);

public sealed record WinGetInstallResult(
    RestoreExecutionState State,
    string ReasonCode,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    bool OutputTruncated,
    bool RequiresRestart);

public sealed record EnvironmentWriteRequest(
    string Name,
    string? Value,
    EnvironmentVariableScope Scope,
    bool IsPathEntry,
    bool IsElevated);

public sealed record EnvironmentWriteResult(
    RestoreExecutionState State,
    string ReasonCode,
    bool Changed,
    bool LengthWarning);

public sealed record RegistryWriteRequest(
    string Hive,
    string KeyPath,
    string ValueName,
    RegistryValueDataKind Kind,
    string? Value,
    bool IsElevated);

public sealed record RegistryWriteResult(
    RestoreExecutionState State,
    string ReasonCode,
    bool Changed);

public sealed record FileRestoreRequest(
    string SnapshotRootDirectory,
    string SourceRelativePath,
    string DestinationPath,
    IReadOnlyList<string> ApprovedDestinationRoots,
    string ExpectedSha256,
    long MaximumFileBytes,
    bool IsExplicitlySelected,
    RestoreFileConflictBehavior ConflictBehavior,
    string RollbackDirectory);

public sealed record FileRestoreResult(
    RestoreExecutionState State,
    string ReasonCode,
    string? RestoredPath,
    string? BackupPath,
    bool ConflictRequiresConfirmation);

public sealed record RestoreJournalEntry(
    string SessionId,
    string ActionId,
    RestoreActionType ActionType,
    DateTimeOffset RecordedAtUtc,
    string? CurrentValueHash,
    string ReasonCode,
    RestoreAction? Action = null,
    FileRestoreRequest? FileRequest = null);

public sealed record ElevatedRestorePlanEnvelope(
    int SchemaVersion,
    string SessionId,
    DateTimeOffset ExpiresAtUtc,
    string TokenSha256,
    RestorePlan Plan,
    IReadOnlyList<FileRestoreRequest> FileRequests)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record ElevatedPlanLaunchRequest(
    string PlanFilePath,
    string PlanSha256,
    string SingleUseToken,
    string SessionId);

public sealed record ElevatedPlanCreationResult(
    ElevatedPlanLaunchRequest LaunchRequest,
    DateTimeOffset ExpiresAtUtc);

public sealed record ElevatedExecutorArguments(
    string PlanFilePath,
    string PlanSha256,
    string SingleUseToken,
    string SessionId);
