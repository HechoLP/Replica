using Replica.Core.Rollback;

namespace Replica.Core.Services;

public interface IRollbackService
{
    Task<IReadOnlyList<RollbackSessionSummary>> GetRecentSessionsAsync(
        int maximumCount,
        CancellationToken cancellationToken);

    Task<RollbackPlan> CreatePlanAsync(
        string sessionId,
        IReadOnlyCollection<string>? selectedActionIds,
        CancellationToken cancellationToken);

    Task<RollbackExecutionResult> ExecuteAsync(
        RollbackPlan approvedPlan,
        IProgress<RollbackProgress>? progress,
        CancellationToken cancellationToken);
}
