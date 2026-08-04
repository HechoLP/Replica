using Microsoft.Extensions.DependencyInjection;
using Replica.App.Bootstrap;
using Replica.App.ViewModels;
using Replica.Core.Services;
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
    }
}
