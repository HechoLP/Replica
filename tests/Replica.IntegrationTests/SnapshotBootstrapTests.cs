using Microsoft.Extensions.DependencyInjection;
using Replica.App.Bootstrap;
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
}
