using Replica.Core.Execution;
using Replica.Core.Planning;

namespace Replica.Core.Services;

public interface IElevationService
{
    Task<RestoreActionExecutionResult> ExecuteAdministratorActionAsync(
        RestorePlan approvedPlan,
        RestoreAction action,
        IRestoreExecutionContext context,
        CancellationToken cancellationToken);
}

public interface IWinGetInstaller
{
    Task<WinGetInstallResult> InstallAsync(
        WinGetInstallRequest request,
        CancellationToken cancellationToken);
}

public interface IEnvironmentWriter
{
    Task<EnvironmentWriteResult> WriteAsync(
        EnvironmentWriteRequest request,
        CancellationToken cancellationToken);
}

public interface IRegistryWriter
{
    Task<RegistryWriteResult> WriteAsync(
        RegistryWriteRequest request,
        CancellationToken cancellationToken);
}

public interface IFileRestoreService
{
    Task<FileRestoreResult> RestoreAsync(
        FileRestoreRequest request,
        CancellationToken cancellationToken);
}

public interface IElevatedPlanStore
{
    Task<ElevatedPlanCreationResult> CreateAsync(
        RestorePlan approvedPlan,
        RestoreAction administratorAction,
        IRestoreExecutionContext context,
        CancellationToken cancellationToken);

    Task<ElevatedRestorePlanEnvelope> ConsumeAsync(
        ElevatedExecutorArguments arguments,
        CancellationToken cancellationToken);

    Task DiscardAsync(
        ElevatedPlanLaunchRequest request,
        CancellationToken cancellationToken);
}

public interface IElevatedProcessLauncher
{
    Task<int> LaunchAsync(
        ElevatedPlanLaunchRequest request,
        CancellationToken cancellationToken);
}

public interface IElevatedExecutorHost
{
    Task<int> RunAsync(
        ElevatedExecutorArguments arguments,
        CancellationToken cancellationToken);
}
