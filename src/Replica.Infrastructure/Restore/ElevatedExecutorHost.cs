using System.Security.Principal;
using Replica.Core.Execution;
using Replica.Core.Planning;
using Replica.Core.Services;

namespace Replica.Infrastructure.Restore;

public static class ElevatedExecutorArgumentsParser
{
    public static bool IsElevatedRequest(IReadOnlyList<string> arguments)
    {
        return arguments.Count > 0 &&
            arguments[0].Equals("--elevated-executor", StringComparison.Ordinal);
    }

    public static bool TryParse(
        IReadOnlyList<string> arguments,
        out ElevatedExecutorArguments? result)
    {
        result = null;
        if (arguments.Count != 8 ||
            !arguments[0].Equals("--elevated-executor", StringComparison.Ordinal) ||
            !arguments[2].Equals("--plan-sha256", StringComparison.Ordinal) ||
            !arguments[4].Equals("--single-use-token", StringComparison.Ordinal) ||
            !arguments[6].Equals("--session", StringComparison.Ordinal))
        {
            return false;
        }

        result = new ElevatedExecutorArguments(
            arguments[1],
            arguments[3],
            arguments[5],
            arguments[7]);
        return true;
    }
}

public sealed class ElevatedExecutorHost : IElevatedExecutorHost
{
    private readonly IRestoreExecutor _executor;
    private readonly IRestoreJournal _journal;
    private readonly IElevatedPlanStore _planStore;
    private readonly IRestoreProgressReporter _progressReporter;

    public ElevatedExecutorHost(
        IElevatedPlanStore planStore,
        IRestoreExecutor executor,
        IRestoreJournal journal,
        IRestoreProgressReporter progressReporter)
    {
        _planStore = planStore;
        _executor = executor;
        _journal = journal;
        _progressReporter = progressReporter;
    }

    public async Task<int> RunAsync(
        ElevatedExecutorArguments arguments,
        CancellationToken cancellationToken)
    {
        if (!IsAdministrator())
        {
            return 5;
        }

        try
        {
            ElevatedRestorePlanEnvelope envelope = await _planStore.ConsumeAsync(
                arguments,
                cancellationToken).ConfigureAwait(false);
            RestoreAction action = envelope.Plan.Actions[0];
            IEnumerable<KeyValuePair<string, FileRestoreRequest>> mappings =
                envelope.FileRequests.Count == 1
                    ? [new KeyValuePair<string, FileRestoreRequest>(action.Id, envelope.FileRequests[0])]
                    : [];
            RestoreExecutionContext context = new(
                envelope.SessionId,
                isElevated: true,
                _journal,
                mappings);
            RestoreExecutionResult result = await _executor.ExecuteAsync(
                envelope.Plan,
                context,
                _progressReporter,
                cancellationToken).ConfigureAwait(false);
            return result.Actions.All(item => item.State is
                RestoreExecutionState.Succeeded or
                RestoreExecutionState.Skipped or
                RestoreExecutionState.RequiresRestart)
                ? 0
                : 1;
        }
        catch (OperationCanceledException)
        {
            return 2;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return 1;
        }
    }

    private static bool IsAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
