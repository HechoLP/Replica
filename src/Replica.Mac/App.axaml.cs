using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using Replica.Core.Services;
using Replica.Core.Snapshots;
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
    private string? lastActivatedSnapshotPath;

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
            MainViewModel viewModel = services.GetRequiredService<MainViewModel>();
            MainWindow window = new()
            {
                DataContext = viewModel,
            };
            services.GetRequiredService<MacFileDialogService>().Attach(window);
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => services?.Dispose();
            if (this.TryGetFeature<IActivatableLifetime>() is { } activatableLifetime)
            {
                activatableLifetime.Activated += (_, eventArgs) =>
                {
                    if (eventArgs is FileActivatedEventArgs fileArguments)
                    {
                        TryOpenActivatedSnapshot(fileArguments.Files, viewModel);
                    }
                };
            }

            TryOpenActivatedSnapshot(desktop.Args, viewModel);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void TryOpenActivatedSnapshot(
        IReadOnlyList<IStorageItem> files,
        MainViewModel viewModel)
    {
        if (files.Count != 1 || !files[0].Path.IsFile)
        {
            return;
        }

        TryOpenActivatedSnapshot([files[0].Path.LocalPath], viewModel);
    }

    private void TryOpenActivatedSnapshot(IReadOnlyList<string>? arguments, MainViewModel viewModel)
    {
        if (arguments is null)
        {
            return;
        }

        bool parsed = SnapshotOpenArgumentsParser.TryParse(arguments, out string? snapshotPath) ||
            SnapshotOpenArgumentsParser.TryParseAssociatedFile(arguments, out snapshotPath);
        if (!parsed || snapshotPath is null ||
            snapshotPath.Equals(lastActivatedSnapshotPath, StringComparison.Ordinal))
        {
            return;
        }

        lastActivatedSnapshotPath = snapshotPath;
        Task operation = viewModel.OpenSnapshotPathAsync(snapshotPath);
        _ = ClearActivatedSnapshotWhenCompleteAsync(operation, snapshotPath);
    }

    private async Task ClearActivatedSnapshotWhenCompleteAsync(Task operation, string snapshotPath)
    {
        try
        {
            await operation;
        }
        finally
        {
            if (snapshotPath.Equals(lastActivatedSnapshotPath, StringComparison.Ordinal))
            {
                lastActivatedSnapshotPath = null;
            }
        }
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
