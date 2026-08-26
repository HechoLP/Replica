using Replica.Core.Planning;
using Replica.Core.Rollback;
using Replica.Core.Services;

namespace Replica.Core.Execution;

public sealed class RestoreExecutor : IRestoreExecutor
{
    private readonly IElevationService _elevationService;
    private readonly IReadOnlyList<IRestoreActionHandler> _handlers;

    public RestoreExecutor(
        IEnumerable<IRestoreActionHandler> handlers,
        IElevationService elevationService)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        _handlers = handlers.ToArray();
        _elevationService = elevationService;
    }

    public async Task<RestoreExecutionResult> ExecuteAsync(
        RestorePlan plan,
        IRestoreExecutionContext context,
        IRestoreProgressReporter progressReporter,
        CancellationToken cancellationToken)
    {
        ValidateExecution(plan, context, progressReporter);
        List<RestoreActionExecutionResult> results = [];
        Dictionary<string, RestoreActionExecutionResult> resultById = new(StringComparer.Ordinal);
        int total = plan.Actions.Count;

        for (int index = 0; index < plan.Actions.Count; index++)
        {
            RestoreAction action = plan.Actions[index];
            if (cancellationToken.IsCancellationRequested)
            {
                AddCancelledRemainder(plan.Actions, index, results, resultById);
                break;
            }

            RestoreActionExecutionResult result;
            if (!action.IsSelected)
            {
                result = Result(action, RestoreExecutionState.Skipped, "ActionNotSelected", "The action was not selected.");
            }
            else if (action.IsManualOnly)
            {
                result = Result(action, RestoreExecutionState.Skipped, "ManualActionRequired", "This action requires manual review.");
            }
            else if (!DependenciesSucceeded(action, resultById))
            {
                result = Result(
                    action,
                    RestoreExecutionState.Skipped,
                    "DependencyNotSucceeded",
                    "A required action did not complete successfully.");
            }
            else
            {
                await progressReporter.ReportAsync(
                    new RestoreProgress(
                        context.SessionId,
                        action.Id,
                        action.Name,
                        RestoreExecutionState.Running,
                        index,
                        total,
                        "Restore action is running."),
                    cancellationToken).ConfigureAwait(false);
                result = await ExecuteActionAsync(plan, action, context, cancellationToken)
                    .ConfigureAwait(false);
            }

            results.Add(result);
            resultById[action.Id] = result;
            await progressReporter.ReportAsync(
                new RestoreProgress(
                    context.SessionId,
                    action.Id,
                    action.Name,
                    result.State,
                    index + 1,
                    total,
                    result.Message),
                CancellationToken.None).ConfigureAwait(false);
            if (result.ReasonCode == "RecoveryJournalFinalizationFailed")
            {
                AddSafetyStoppedRemainder(plan.Actions, index + 1, results, resultById);
                break;
            }
        }

        bool cancelled = results.Any(result => result.State == RestoreExecutionState.Cancelled);
        return new RestoreExecutionResult(
            context.SessionId,
            results,
            results.Any(result => result.RequiresRestart ||
                result.State == RestoreExecutionState.RequiresRestart),
            cancelled);
    }

    private async Task<RestoreActionExecutionResult> ExecuteActionAsync(
        RestorePlan plan,
        RestoreAction action,
        IRestoreExecutionContext context,
        CancellationToken cancellationToken)
    {
        bool journalPrepared = false;
        try
        {
            if (action.RequiresAdministrator && !context.IsElevated)
            {
                return await _elevationService.ExecuteAdministratorActionAsync(
                    plan,
                    action,
                    context,
                    cancellationToken).ConfigureAwait(false);
            }

            if (IsMutation(action.Type))
            {
                await context.Journal.RecordBeforeMutationAsync(
                    new RestoreJournalEntry(
                        context.SessionId,
                        action.Id,
                        action.Type,
                        DateTimeOffset.UtcNow,
                        null,
                        "BeforeMutation",
                        action,
                        context.GetFileRestoreRequest(action.Id)),
                    cancellationToken).ConfigureAwait(false);
                journalPrepared = true;
            }

            IRestoreActionHandler? handler = _handlers.FirstOrDefault(candidate =>
                candidate.CanHandle(action.Type));
            if (handler is null)
            {
                return Result(
                    action,
                    RestoreExecutionState.Failed,
                    "NoAllowListedHandler",
                    "No allow-listed handler is registered for this action type.");
            }

            RestoreActionExecutionResult result = await handler.ExecuteAsync(
                action,
                context,
                cancellationToken).ConfigureAwait(false);
            if (!result.ActionId.Equals(action.Id, StringComparison.Ordinal))
            {
                RestoreActionExecutionResult mismatch = Result(
                    action,
                    RestoreExecutionState.Failed,
                    "HandlerResultMismatch",
                    "The action handler returned an invalid result.");
                if (IsMutation(action.Type))
                {
                    return await FinalizeJournalOrFailAsync(context, action, mismatch)
                        .ConfigureAwait(false);
                }

                return mismatch;
            }

            if (IsMutation(action.Type))
            {
                return await FinalizeJournalOrFailAsync(context, action, result)
                    .ConfigureAwait(false);
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            RestoreActionExecutionResult cancelled = Result(
                action,
                RestoreExecutionState.Cancelled,
                "ActionCancelled",
                "The action was cancelled.");
            return journalPrepared
                ? await FinalizeJournalOrFailAsync(context, action, cancelled).ConfigureAwait(false)
                : cancelled;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            RestoreActionExecutionResult failed = Result(
                action,
                RestoreExecutionState.Failed,
                "ActionExecutionFailed",
                "The restore action failed without exposing system or payload details.");
            return journalPrepared
                ? await FinalizeJournalOrFailAsync(context, action, failed).ConfigureAwait(false)
                : failed;
        }
    }

    private static async Task<RestoreActionExecutionResult> FinalizeJournalOrFailAsync(
        IRestoreExecutionContext context,
        RestoreAction action,
        RestoreActionExecutionResult result)
    {
        try
        {
            await UpdateJournalStateAsync(context, action, result).ConfigureAwait(false);
            return result;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Result(
                action,
                RestoreExecutionState.Failed,
                "RecoveryJournalFinalizationFailed",
                "Replica stopped because the post-change recovery record could not be committed.");
        }
    }

    private static async Task UpdateJournalStateAsync(
        IRestoreExecutionContext context,
        RestoreAction action,
        RestoreActionExecutionResult result)
    {
        RollbackJournalState state = result.State switch
        {
            RestoreExecutionState.Succeeded or RestoreExecutionState.RequiresRestart =>
                RollbackJournalState.Applied,
            RestoreExecutionState.Skipped => RollbackJournalState.Verified,
            RestoreExecutionState.Failed or RestoreExecutionState.Cancelled =>
                RollbackJournalState.RollbackPending,
            _ => RollbackJournalState.Applied,
        };
        await context.Journal.MarkActionStateAsync(
            context.SessionId,
            action.Id,
            state,
            result.MutationTargetPath,
            CancellationToken.None).ConfigureAwait(false);
        if (state == RollbackJournalState.Applied)
        {
            await context.Journal.MarkActionStateAsync(
                context.SessionId,
                action.Id,
                RollbackJournalState.Verified,
                result.MutationTargetPath,
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static void ValidateExecution(
        RestorePlan plan,
        IRestoreExecutionContext context,
        IRestoreProgressReporter progressReporter)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(progressReporter);
        if (!plan.IsApproved || plan.ReviewStatus != RestorePlanReviewStatus.Approved)
        {
            throw new InvalidOperationException("Only an explicitly approved restore plan can execute.");
        }

        if (string.IsNullOrWhiteSpace(context.SessionId) || context.Journal is null || plan.Actions is null)
        {
            throw new InvalidOperationException("The restore execution context is invalid.");
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (RestoreAction action in plan.Actions)
        {
            if (action is null ||
                string.IsNullOrWhiteSpace(action.Id) ||
                !Enum.IsDefined(action.Type) ||
                action.Dependencies is null ||
                !seen.Add(action.Id) ||
                action.Dependencies.Any(dependency => !seen.Contains(dependency)))
            {
                throw new InvalidOperationException("The restore plan is invalid or not topologically ordered.");
            }

            if (action.IsSelected &&
                action.Dependencies.Any(dependency =>
                    !plan.Actions.First(candidate => candidate.Id == dependency).IsSelected))
            {
                throw new InvalidOperationException("A selected restore action has an unselected dependency.");
            }
        }
    }

    private static bool DependenciesSucceeded(
        RestoreAction action,
        IReadOnlyDictionary<string, RestoreActionExecutionResult> results)
    {
        return action.Dependencies.All(dependency =>
            results.TryGetValue(dependency, out RestoreActionExecutionResult? result) &&
            result.State is RestoreExecutionState.Succeeded or RestoreExecutionState.RequiresRestart);
    }

    private static bool IsMutation(RestoreActionType type)
    {
        return type is not (
            RestoreActionType.ManualInstruction or
            RestoreActionType.Validate or
            RestoreActionType.RestartRequired);
    }

    private static void AddCancelledRemainder(
        IReadOnlyList<RestoreAction> actions,
        int startIndex,
        ICollection<RestoreActionExecutionResult> results,
        IDictionary<string, RestoreActionExecutionResult> resultById)
    {
        for (int index = startIndex; index < actions.Count; index++)
        {
            RestoreAction action = actions[index];
            RestoreActionExecutionResult result = Result(
                action,
                action.IsSelected ? RestoreExecutionState.Cancelled : RestoreExecutionState.Skipped,
                action.IsSelected ? "ExecutionCancelled" : "ActionNotSelected",
                action.IsSelected ? "Execution was cancelled before this action started." : "The action was not selected.");
            results.Add(result);
            resultById[action.Id] = result;
        }
    }

    private static void AddSafetyStoppedRemainder(
        IReadOnlyList<RestoreAction> actions,
        int startIndex,
        ICollection<RestoreActionExecutionResult> results,
        IDictionary<string, RestoreActionExecutionResult> resultById)
    {
        for (int index = startIndex; index < actions.Count; index++)
        {
            RestoreAction action = actions[index];
            RestoreActionExecutionResult result = Result(
                action,
                RestoreExecutionState.Skipped,
                "JournalSafetyStop",
                "Execution stopped before this action because a recovery record could not be committed.");
            results.Add(result);
            resultById[action.Id] = result;
        }
    }

    private static RestoreActionExecutionResult Result(
        RestoreAction action,
        RestoreExecutionState state,
        string reasonCode,
        string message)
    {
        return new RestoreActionExecutionResult(action.Id, state, reasonCode, message);
    }
}
