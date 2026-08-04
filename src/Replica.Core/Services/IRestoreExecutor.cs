using Replica.Core.Execution;
using Replica.Core.Planning;

namespace Replica.Core.Services;

public interface IRestoreExecutor
{
    Task<RestoreExecutionResult> ExecuteAsync(
        RestorePlan plan,
        IRestoreExecutionContext context,
        IRestoreProgressReporter progressReporter,
        CancellationToken cancellationToken);
}

public interface IRestoreActionHandler
{
    bool CanHandle(RestoreActionType actionType);

    Task<RestoreActionExecutionResult> ExecuteAsync(
        RestoreAction action,
        IRestoreExecutionContext context,
        CancellationToken cancellationToken);
}

public interface IRestoreExecutionContext
{
    string SessionId { get; }

    bool IsElevated { get; }

    IRestoreJournal Journal { get; }

    FileRestoreRequest? GetFileRestoreRequest(string actionId);
}

public interface IRestoreProgressReporter
{
    Task ReportAsync(RestoreProgress progress, CancellationToken cancellationToken);
}

public interface IRestoreJournal
{
    Task RecordBeforeMutationAsync(
        RestoreJournalEntry entry,
        CancellationToken cancellationToken);
}
