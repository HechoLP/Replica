using System.ComponentModel;
using System.Diagnostics;
using Replica.Core.Execution;
using Replica.Core.Planning;
using Replica.Core.Services;

namespace Replica.Infrastructure.Restore;

public sealed class ElevationService : IElevationService
{
    private readonly IElevatedPlanStore _planStore;
    private readonly IElevatedProcessLauncher _processLauncher;

    public ElevationService(
        IElevatedPlanStore planStore,
        IElevatedProcessLauncher processLauncher)
    {
        _planStore = planStore;
        _processLauncher = processLauncher;
    }

    public async Task<RestoreActionExecutionResult> ExecuteAdministratorActionAsync(
        RestorePlan approvedPlan,
        RestoreAction action,
        IRestoreExecutionContext context,
        CancellationToken cancellationToken)
    {
        ElevatedPlanCreationResult created = await _planStore.CreateAsync(
            approvedPlan,
            action,
            context,
            cancellationToken).ConfigureAwait(false);
        try
        {
            int exitCode = await _processLauncher.LaunchAsync(
                created.LaunchRequest,
                cancellationToken).ConfigureAwait(false);
            if (exitCode == 0)
            {
                return new RestoreActionExecutionResult(
                    action.Id,
                    action.RequiresRestart
                        ? RestoreExecutionState.RequiresRestart
                        : RestoreExecutionState.Succeeded,
                    "ElevatedActionCompleted",
                    "The administrator action completed in the restricted executor.",
                    action.RequiresRestart,
                    exitCode);
            }

            return new RestoreActionExecutionResult(
                action.Id,
                RestoreExecutionState.Failed,
                exitCode == 1223 ? "ElevationCancelled" : "ElevatedActionFailed",
                "The administrator action did not complete.",
                false,
                exitCode);
        }
        finally
        {
            await _planStore.DiscardAsync(created.LaunchRequest, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }
}

public sealed class ElevatedProcessLauncher : IElevatedProcessLauncher
{
    private readonly Func<ProcessStartInfo, CancellationToken, Task<int>> _launchAndWait;

    public ElevatedProcessLauncher()
        : this(LaunchAndWaitAsync)
    {
    }

    internal ElevatedProcessLauncher(
        Func<ProcessStartInfo, CancellationToken, Task<int>> launchAndWait)
    {
        _launchAndWait = launchAndWait;
    }

    public async Task<int> LaunchAsync(
        ElevatedPlanLaunchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        string executable = System.Environment.ProcessPath ??
            throw new InvalidOperationException("Replica executable path is unavailable.");
        ProcessStartInfo startInfo = new(executable)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        startInfo.ArgumentList.Add("--elevated-executor");
        startInfo.ArgumentList.Add(request.PlanFilePath);
        startInfo.ArgumentList.Add("--plan-sha256");
        startInfo.ArgumentList.Add(request.PlanSha256);
        startInfo.ArgumentList.Add("--single-use-token");
        startInfo.ArgumentList.Add(request.SingleUseToken);
        startInfo.ArgumentList.Add("--session");
        startInfo.ArgumentList.Add(request.SessionId);

        try
        {
            // After the elevated child starts, it owns a possible machine mutation and the
            // mandatory post-mutation journal commit. Parent cancellation must wait for that
            // single bounded handler to cross its durable safety boundary instead of killing it.
            return await _launchAndWait(startInfo, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return 1223;
        }
    }

    private static async Task<int> LaunchAndWaitAsync(
        ProcessStartInfo startInfo,
        CancellationToken safetyBoundaryToken)
    {
        using Process process = Process.Start(startInfo) ??
            throw new InvalidOperationException("The elevated Replica process could not start.");
        await process.WaitForExitAsync(safetyBoundaryToken).ConfigureAwait(false);
        return process.ExitCode;
    }
}
