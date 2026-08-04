using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Replica.App.Services;
using Replica.App.ViewModels;
using Replica.Core.Diffing;
using Replica.Core.Execution;
using Replica.Core.Matching;
using Replica.Core.Models;
using Replica.Core.Planning;
using Replica.Core.Plugins;
using Replica.Core.Services;
using Replica.Infrastructure.Environment;
using Replica.Infrastructure.Paths;
using Replica.Infrastructure.Restore;
using Replica.Infrastructure.Scanning;
using Replica.Infrastructure.Snapshots;
using Replica.Infrastructure.Updates;
using Replica.Plugins.BuiltIn;

namespace Replica.App.Bootstrap;

public static class AppBootstrapper
{
    public static ServiceProvider BuildServices()
    {
        ServiceCollection services = new();

        services.AddLogging(builder => builder.AddDebug());
        services.AddSingleton(TimeProvider.System);

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
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IWinGetScanner, WinGetScanner>();
        services.AddSingleton<IRegistryApplicationSource, WindowsRegistryApplicationSource>();
        services.AddSingleton<IRegistryApplicationScanner, RegistryApplicationScanner>();
        services.AddSingleton<IMsixApplicationScanner, MsixApplicationScanner>();
        services.AddSingleton<IApplicationScanner, ApplicationScanner>();
        services.AddSingleton<IEnvironmentValueSource, WindowsEnvironmentValueSource>();
        services.AddSingleton<IEnvironmentVariableScanner, EnvironmentVariableScanner>();
        services.AddSingleton<IFontInventorySource, WindowsFontInventorySource>();
        services.AddSingleton<IFontScanner, FontScanner>();
        services.AddSingleton<IWindowsInfoScanner, WindowsInfoScanner>();
        foreach (IBuiltInPlugin plugin in BuiltInPluginCatalog.CreateDefault())
        {
            services.AddSingleton(plugin);
        }

        services.AddSingleton<IEnvironmentScanner, EnvironmentScanner>();
        services.AddSingleton<IApplicationIdentityNormalizer, ApplicationIdentityNormalizer>();
        services.AddSingleton<IApplicationMatcher, ApplicationMatcher>();
        services.AddSingleton<IApplicationVersionComparer, ApplicationVersionComparer>();
        services.AddSingleton<IApplicationAutomationPolicy, ApplicationAutomationPolicy>();
        services.AddSingleton<IEnvironmentDiffEngine, EnvironmentDiffEngine>();
        services.AddSingleton<IRestorePlanner, RestorePlanner>();
        services.AddSingleton<IEnvironmentVariableStore, WindowsEnvironmentVariableStore>();
        services.AddSingleton<IEnvironmentChangeNotifier, WindowsEnvironmentChangeNotifier>();
        services.AddSingleton<IEnvironmentWriter, EnvironmentWriter>();
        services.AddSingleton<IRegistryWriteAllowList, BuiltInRegistryWriteAllowList>();
        services.AddSingleton<IRegistryValueStore, WindowsRegistryValueStore>();
        services.AddSingleton<IRegistryWriter, RegistryWriter>();
        services.AddSingleton<IFileRestoreService, FileRestoreService>();
        services.AddSingleton<IWinGetInstaller, WinGetInstaller>();
        services.AddSingleton<RollbackJournalService>();
        services.AddSingleton<IRestoreJournal>(provider =>
            provider.GetRequiredService<RollbackJournalService>());
        services.AddSingleton<IRollbackService>(provider =>
            provider.GetRequiredService<RollbackJournalService>());
        services.AddSingleton<IRestoreProgressReporter, NullRestoreProgressReporter>();
        services.AddSingleton<IElevatedPlanStore, ElevatedPlanStore>();
        services.AddSingleton<IElevatedProcessLauncher, ElevatedProcessLauncher>();
        services.AddSingleton<IElevationService, ElevationService>();
        services.AddSingleton<IRestoreActionHandler, PackageRestoreActionHandler>();
        services.AddSingleton<IRestoreActionHandler, EnvironmentRestoreActionHandler>();
        services.AddSingleton<IRestoreActionHandler, RegistryRestoreActionHandler>();
        services.AddSingleton<IRestoreActionHandler, FileRestoreActionHandler>();
        services.AddSingleton<IRestoreActionHandler, ValidationRestoreActionHandler>();
        services.AddSingleton<IRestoreExecutor, RestoreExecutor>();
        services.AddSingleton<IElevatedExecutorHost, ElevatedExecutorHost>();

        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<ILocalizationService, ResourceLocalizationService>();
        services.AddSingleton<IThemeService, WpfThemeService>();
        services.AddSingleton<IGlobalExceptionHandler, GlobalExceptionHandler>();

        services.AddSingleton<MainViewModel>();
        services.AddSingleton<DiffViewerViewModel>();
        services.AddSingleton<RestoreDryRunViewModel>();
        services.AddSingleton<RollbackViewModel>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });
    }
}
