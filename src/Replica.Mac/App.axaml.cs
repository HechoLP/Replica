using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Replica.Core.Services;
using Replica.Infrastructure.Snapshots;
using Replica.Mac.Infrastructure.Paths;
using Replica.Mac.Infrastructure.Scanning;
using Replica.Mac.Infrastructure.Snapshots;
using Replica.Mac.Infrastructure.Updates;
using Replica.Mac.Services;
using Replica.Mac.ViewModels;

namespace Replica.Mac;

public sealed partial class App : Application
{
    private ServiceProvider? services;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            ServiceCollection registrations = new();
            ConfigureServices(registrations);
            services = registrations.BuildServiceProvider(validateScopes: true);
            MainWindow window = new()
            {
                DataContext = services.GetRequiredService<MainViewModel>(),
            };
            services.GetRequiredService<MacFileDialogService>().Attach(window);
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IReplicaPathProvider, MacReplicaPathProvider>();
        services.AddSingleton<IMacSystemInfoSource, MacSystemInfoSource>();
        services.AddSingleton<IMacApplicationSource, MacApplicationSource>();
        services.AddSingleton<IHomebrewInventorySource, HomebrewInventorySource>();
        services.AddSingleton<IMacEnvironmentSource, MacEnvironmentSource>();
        services.AddSingleton<IMacFontSource, MacFontSource>();
        services.AddSingleton<IPlatformEnvironmentScanner, MacEnvironmentScanner>();
        services.AddSingleton<ISnapshotSelectionEstimator, EmptySnapshotSelectionEstimator>();
        services.AddSingleton<ISnapshotReader, ReplicaSnapshotReader>();
        services.AddSingleton<ISnapshotWriter, ReplicaSnapshotWriter>();
        services.AddSingleton<IMacLightweightSnapshotService, MacLightweightSnapshotService>();
        services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(20) });
        services.AddSingleton<IMacUpdateService, MacUpdateService>();
        services.AddSingleton<MacFileDialogService>();
        services.AddSingleton<IMacFileDialogService>(provider => provider.GetRequiredService<MacFileDialogService>());
        services.AddSingleton<MainViewModel>();
    }
}
