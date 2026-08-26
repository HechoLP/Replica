using Replica.Core.Execution;
using Replica.Core.Planning;

namespace Replica.Core.Rollback;

public enum RollbackJournalState
{
    Prepared,
    Applied,
    Verified,
    RollbackPending,
    RolledBack,
    RollbackFailed,
}

public enum RollbackItemKind
{
    File,
    CreatedFile,
    EnvironmentVariable,
    Path,
    Registry,
    Plugin,
    ApplicationInstallation,
}

public enum RollbackPlanReviewStatus
{
    PendingReview,
    Approved,
    Rejected,
}

public sealed record RollbackJournalItem(
    string ActionId,
    RestoreActionType ActionType,
    string Name,
    RollbackItemKind Kind,
    RollbackJournalState State,
    bool CanRollbackAutomatically,
    bool RequiresAdministrator,
    string? DataPath,
    string? BackupPath,
    string? AppliedValueSha256,
    string? ManualInstruction);

public sealed record RollbackJournalManifest(
    int SchemaVersion,
    string SessionId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    RollbackJournalState State,
    IReadOnlyList<RollbackJournalItem> Items)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record RollbackEnvironmentData(
    string Name,
    EnvironmentVariableScope Scope,
    bool ExistedBefore,
    string? OriginalValue);

public sealed record RollbackRegistryData(
    string Hive,
    string KeyPath,
    string ValueName,
    RegistryValueDataKind Kind,
    bool ExistedBefore,
    string? OriginalValue);

public sealed record RollbackFileData(
    string DestinationPath,
    IReadOnlyList<string> ApprovedDestinationRoots,
    bool ExistedBefore,
    string? OriginalSha256,
    string AppliedSha256);

public sealed record RollbackSessionSummary(
    string SessionId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    RollbackJournalState State,
    int ItemCount,
    int AutomaticItemCount,
    int ManualItemCount);

public sealed record RollbackPreviewItem(
    string ActionId,
    string Name,
    RollbackItemKind Kind,
    RollbackJournalState State,
    bool CanRollbackAutomatically,
    bool RequiresAdministrator,
    bool IsSelected,
    string? ManualInstruction,
    string Target = "",
    string Effect = "",
    string? ExpectedCurrentSha256 = null);

public sealed record RollbackPlan(
    string SessionId,
    IReadOnlyList<RollbackPreviewItem> Items,
    RollbackPlanReviewStatus ReviewStatus,
    DateTimeOffset CreatedAtUtc)
{
    public RollbackPlan Approve()
    {
        if (ReviewStatus != RollbackPlanReviewStatus.PendingReview)
        {
            throw new InvalidOperationException("Only a pending rollback plan can be approved.");
        }

        return this with { ReviewStatus = RollbackPlanReviewStatus.Approved };
    }

    public RollbackPlan Reject()
    {
        if (ReviewStatus != RollbackPlanReviewStatus.PendingReview)
        {
            throw new InvalidOperationException("Only a pending rollback plan can be rejected.");
        }

        return this with { ReviewStatus = RollbackPlanReviewStatus.Rejected };
    }
}

public sealed record RollbackProgress(
    string SessionId,
    string ActionId,
    string Name,
    RollbackJournalState State,
    int CompletedItems,
    int TotalItems,
    string Message);

public sealed record RollbackItemResult(
    string ActionId,
    RollbackJournalState State,
    string ReasonCode,
    string Message);

public sealed record RollbackExecutionResult(
    string SessionId,
    RollbackJournalState State,
    IReadOnlyList<RollbackItemResult> Items,
    bool WasPartial,
    bool WasAlreadyRolledBack);
