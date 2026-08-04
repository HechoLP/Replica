using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Replica.App.Services;
using Replica.App.ViewModels;
using Replica.Core.Models;
using Replica.Core.Services;
using Replica.Infrastructure.Environment;
using Replica.Infrastructure.Paths;
using Replica.Infrastructure.Snapshots;
using Replica.Infrastructure.Updates;

namespace Replica.App.Bootstrap;

public static class AppBootstrapper
{
    public static ServiceProvider BuildServices()
    {
        ServiceCollection services = new();

        services.AddLogging(builder => builder.AddDebug());

        services.AddSingleton(ReleaseRepositoryOptions.Replica);
        services.AddSingleton<IOperatingSystemInfo, SystemOperatingSystemInfo>();
        services.AddSingleton<IWindowsCompatibilityService, WindowsCompatibilityService>();
        services.AddSingleton<IReplicaPathProvider, ReplicaPathProvider>();
        services.AddSingleton<IAppVersionService, AppVersionService>();
        services.AddSingleton<IReleaseVersionComparer, ReleaseVersionComparer>();
        services.AddSingleton<IReleaseProvider, PlaceholderReleaseProvider>();
        services.AddSingleton<IUpdateCheckService, UpdateCheckService>();
        services.AddSingleton<ISnapshotSelectionEstimator, SnapshotSelectionEstimator>();
        services.AddSingleton<ISnapshotReader, ReplicaSnapshotReader>();
        services.AddSingleton<ISnapshotWriter, ReplicaSnapshotWriter>();

        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<ILocalizationService, ResourceLocalizationService>();
        services.AddSingleton<IThemeService, WpfThemeService>();
        services.AddSingleton<IGlobalExceptionHandler, GlobalExceptionHandler>();

        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });
    }
}
