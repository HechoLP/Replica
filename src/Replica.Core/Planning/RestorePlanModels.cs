using Replica.Core.Diffing;

namespace Replica.Core.Planning;

public enum RestoreActionType
{
    InstallPackage,
    UpdatePackage,
    RestoreFile,
    MergeJson,
    SetUserEnvironmentVariable,
    SetMachineEnvironmentVariable,
    AddPathEntry,
    RestoreRegistryValue,
    InstallExtension,
    InstallPowerShellModule,
    RestoreSelectedUserFile,
    ManualInstruction,
    Validate,
    RestartRequired,
}

public enum RestorePlanReviewStatus
{
    PendingReview,
    Approved,
    Rejected,
}

public enum RestorePlanningFailure
{
    InvalidInput,
    DuplicateAction,
    DuplicateHint,
    MissingDependency,
    DependencyCycle,
}

public sealed record RestoreAction(
    string Id,
    RestoreActionType Type,
    string Name,
    string Description,
    string? OriginalValue,
    string? CurrentValue,
    string? TargetValue,
    DiffRiskLevel Risk,
    bool RequiresAdministrator,
    bool RequiresRestart,
    bool CanRollback,
    IReadOnlyList<string> Dependencies,
    TimeSpan EstimatedDuration,
    long EstimatedDownloadBytes,
    bool IsSelected,
    bool IsManualOnly,
    DiffArea SourceArea,
    string SourceDiffKey,
    string ReasonCode);

public sealed record RestoreActionReference(DiffArea Area, string DiffKey);

public sealed record RestoreActionHint(
    DiffArea Area,
    string DiffKey,
    IReadOnlyList<RestoreActionReference>? DependsOn = null,
    TimeSpan? EstimatedDuration = null,
    long EstimatedDownloadBytes = 0,
    bool? CanRollback = null);

public sealed record RestorePlanningOptions(IReadOnlyList<RestoreActionHint> Hints)
{
    public static RestorePlanningOptions Empty { get; } = new([]);
}

public sealed record RestoreDryRunSummary(
    int InstallPackageCount,
    int UpdatePackageCount,
    int RestoreSettingsCount,
    int RestoreSelectedUserFileCount,
    int EnvironmentChangeCount,
    int AdministratorActionCount,
    bool RequiresRestart,
    int RollbackCapableActionCount,
    int ManualActionCount,
    int SelectedActionCount,
    TimeSpan EstimatedDuration,
    long EstimatedDownloadBytes);

public sealed record RestorePlan(
    string Id,
    DiffRestoreMode Mode,
    IReadOnlyList<RestoreAction> Actions,
    RestoreDryRunSummary DryRun,
    RestorePlanReviewStatus ReviewStatus,
    DateTimeOffset CreatedAtUtc)
{
    public bool RequiresUserConfirmation => ReviewStatus == RestorePlanReviewStatus.PendingReview;

    public bool IsApproved => ReviewStatus == RestorePlanReviewStatus.Approved;

    public RestorePlan Approve()
    {
        if (ReviewStatus != RestorePlanReviewStatus.PendingReview)
        {
            throw new InvalidOperationException("Only a pending restore plan can be approved.");
        }

        HashSet<string> selectedIds = Actions
            .Where(action => action.IsSelected)
            .Select(action => action.Id)
            .ToHashSet(StringComparer.Ordinal);
        RestoreAction? invalid = Actions.FirstOrDefault(action =>
            action.IsSelected && action.Dependencies.Any(dependency => !selectedIds.Contains(dependency)));
        if (invalid is not null)
        {
            throw new InvalidOperationException(
                $"Selected action '{invalid.Id}' has an unselected dependency.");
        }

        return this with { ReviewStatus = RestorePlanReviewStatus.Approved };
    }

    public RestorePlan Reject()
    {
        if (ReviewStatus != RestorePlanReviewStatus.PendingReview)
        {
            throw new InvalidOperationException("Only a pending restore plan can be rejected.");
        }

        return this with { ReviewStatus = RestorePlanReviewStatus.Rejected };
    }
}

public sealed class RestorePlanningException : InvalidOperationException
{
    public RestorePlanningException(RestorePlanningFailure failure, string message)
        : base(message)
    {
        Failure = failure;
    }

    public RestorePlanningFailure Failure { get; }
}
