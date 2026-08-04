using Microsoft.Extensions.DependencyInjection;
using Replica.App.Bootstrap;
using Replica.App.ViewModels;
using Replica.Core.Execution;
using Replica.Core.Planning;
using Replica.Core.Services;
using Replica.Infrastructure.Restore;
using Replica.Infrastructure.Snapshots;

namespace Replica.IntegrationTests;

public sealed class SnapshotBootstrapTests
{
    [Fact]
    public void SnapshotServicesResolveFromApplicationBootstrapper()
    {
        using ServiceProvider services = AppBootstrapper.BuildServices();

        Assert.IsType<SnapshotSelectionEstimator>(services.GetRequiredService<ISnapshotSelectionEstimator>());
        Assert.IsType<ReplicaSnapshotReader>(services.GetRequiredService<ISnapshotReader>());
        Assert.IsType<ReplicaSnapshotWriter>(services.GetRequiredService<ISnapshotWriter>());
    }

    [Fact]
    public void EnvironmentScannerServicesResolveFromApplicationBootstrapper()
    {
        using ServiceProvider services = AppBootstrapper.BuildServices();

        Assert.NotNull(services.GetRequiredService<IEnvironmentScanner>());
        Assert.NotNull(services.GetRequiredService<IApplicationScanner>());
        Assert.NotNull(services.GetRequiredService<IWinGetScanner>());
        Assert.NotNull(services.GetRequiredService<IRegistryApplicationScanner>());
        Assert.NotNull(services.GetRequiredService<IMsixApplicationScanner>());
        Assert.NotNull(services.GetRequiredService<IEnvironmentVariableScanner>());
        Assert.NotNull(services.GetRequiredService<IFontScanner>());
        Assert.NotNull(services.GetRequiredService<IWindowsInfoScanner>());
        Assert.NotNull(services.GetRequiredService<IProcessRunner>());
    }

    [Fact]
    public void ApplicationMatchingServicesResolveFromApplicationBootstrapper()
    {
        using ServiceProvider services = AppBootstrapper.BuildServices();

        Assert.NotNull(services.GetRequiredService<IApplicationIdentityNormalizer>());
        Assert.NotNull(services.GetRequiredService<IApplicationMatcher>());
        Assert.NotNull(services.GetRequiredService<IApplicationVersionComparer>());
        Assert.NotNull(services.GetRequiredService<IApplicationAutomationPolicy>());
    }

    [Fact]
    public void DiffServicesResolveFromApplicationBootstrapper()
    {
        using ServiceProvider services = AppBootstrapper.BuildServices();

        Assert.NotNull(services.GetRequiredService<IEnvironmentDiffEngine>());
        Assert.NotNull(services.GetRequiredService<DiffViewerViewModel>());
        Assert.IsType<RestorePlanner>(services.GetRequiredService<IRestorePlanner>());
        Assert.NotNull(services.GetRequiredService<RestoreDryRunViewModel>());
    }

    [Fact]
    public void RestoreExecutionServicesResolveFromApplicationBootstrapper()
    {
        using ServiceProvider services = AppBootstrapper.BuildServices();

        Assert.IsType<RestoreExecutor>(services.GetRequiredService<IRestoreExecutor>());
        Assert.NotNull(services.GetRequiredService<IEnumerable<IRestoreActionHandler>>());
        Assert.IsType<WinGetInstaller>(services.GetRequiredService<IWinGetInstaller>());
        Assert.IsType<EnvironmentWriter>(services.GetRequiredService<IEnvironmentWriter>());
        Assert.IsType<RegistryWriter>(services.GetRequiredService<IRegistryWriter>());
        Assert.IsType<FileRestoreService>(services.GetRequiredService<IFileRestoreService>());
        Assert.IsType<ElevationService>(services.GetRequiredService<IElevationService>());
        Assert.IsType<ElevatedPlanStore>(services.GetRequiredService<IElevatedPlanStore>());
        Assert.IsType<ElevatedExecutorHost>(services.GetRequiredService<IElevatedExecutorHost>());
        Assert.NotNull(services.GetRequiredService<IRestoreProgressReporter>());
        Assert.IsType<RollbackJournalService>(services.GetRequiredService<IRollbackService>());
        Assert.NotNull(services.GetRequiredService<RollbackViewModel>());
    }
}
