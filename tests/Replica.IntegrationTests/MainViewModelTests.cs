using System.Globalization;
using Replica.App.ViewModels;
using Replica.Core.Models;
using Replica.Core.Navigation;
using Replica.Core.Scanning;
using Replica.Core.Services;

namespace Replica.IntegrationTests;

public sealed class MainViewModelTests
{
    [Fact]
    public void CreateSnapshotCommand_ShowsAllSnapshotTypesWithoutSystemWork()
    {
        MainViewModel viewModel = CreateViewModel(out _, out _);

        viewModel.CreateSnapshotCommand.Execute(null);

        Assert.True(viewModel.IsSnapshotTypePickerVisible);
        Assert.Equal(3, viewModel.SnapshotTypes.Count);
    }

    [Fact]
    public void PlannedCommand_ShowsMessageInsteadOfThrowing()
    {
        MainViewModel viewModel = CreateViewModel(out FakeDialogService dialog, out _);

        Exception? exception = Record.Exception(
            () => viewModel.OpenSnapshotCommand.Execute(null));

        Assert.Null(exception);
        Assert.Contains("구현 예정", dialog.LastMessage, StringComparison.Ordinal);
        Assert.Contains("변경하지 않습니다", dialog.LastMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScanCurrentComputerCommand_ShowsReadOnlyScanSummary()
    {
        MainViewModel viewModel = CreateViewModel(out FakeDialogService dialog, out _);

        await viewModel.ScanCurrentComputerCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsScanSummaryVisible);
        Assert.False(viewModel.IsScanning);
        Assert.Equal(100, viewModel.ScanProgressPercentage);
        Assert.Contains("프로그램: 3", viewModel.ScanSummaryText, StringComparison.Ordinal);
        Assert.Contains("민감 값 제외: 1", viewModel.ScanSummaryText, StringComparison.Ordinal);
        Assert.Contains("경고: 1", dialog.LastMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void NavigationCommands_UpdateNavigationService()
    {
        MainViewModel viewModel = CreateViewModel(out _, out FakeNavigationService navigation);

        viewModel.ShowSettingsCommand.Execute(null);

        Assert.Equal(NavigationDestination.Settings, navigation.CurrentDestination);
    }

    [Fact]
    public async Task CheckForUpdatesCommand_UsesMockUpdateService()
    {
        FakeUpdateCheckService updateService = new();
        MainViewModel viewModel = CreateViewModel(out FakeDialogService dialog, out _, updateService);

        await viewModel.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.Equal(1, updateService.CallCount);
        Assert.Contains("준비 중", dialog.LastMessage, StringComparison.Ordinal);
    }

    private static MainViewModel CreateViewModel(
        out FakeDialogService dialog,
        out FakeNavigationService navigation,
        IUpdateCheckService? updateService = null)
    {
        dialog = new FakeDialogService();
        navigation = new FakeNavigationService();
        return new MainViewModel(
            new FakeAppVersionService(),
            dialog,
            new FakeLocalizationService(),
            navigation,
            updateService ?? new FakeUpdateCheckService(),
            new FakeWindowsCompatibilityService(),
            new FakeEnvironmentScanner(),
            new DiffViewerViewModel(),
            new RestoreDryRunViewModel());
    }

    private sealed class FakeAppVersionService : IAppVersionService
    {
        public Version CurrentVersion { get; } = new(0, 1, 0);

        public string DisplayVersion => "0.1.0-alpha.1";
    }

    private sealed class FakeDialogService : IDialogService
    {
        public string LastMessage { get; private set; } = string.Empty;

        public void ShowMessage(string title, string message)
        {
            LastMessage = message;
        }
    }

    private sealed class FakeLocalizationService : ILocalizationService
    {
        public CultureInfo CurrentCulture => CultureInfo.InvariantCulture;

        public string GetString(string resourceKey)
        {
            return resourceKey switch
            {
                "ProductName" => "Replica",
                "ProductTagline" => "Clone your Windows setup, not your files.",
                _ => resourceKey,
            };
        }
    }

    private sealed class FakeNavigationService : INavigationService
    {
        public event EventHandler<NavigationChangedEventArgs>? Navigated;

        public NavigationDestination CurrentDestination { get; private set; }

        public void Navigate(NavigationDestination destination)
        {
            CurrentDestination = destination;
            Navigated?.Invoke(this, new NavigationChangedEventArgs(destination));
        }
    }

    private sealed class FakeUpdateCheckService : IUpdateCheckService
    {
        public int CallCount { get; private set; }

        public Task<UpdateCheckResult> CheckForUpdatesAsync(
            bool includePrerelease,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(
                new UpdateCheckResult(
                    UpdateCheckStatus.Unavailable,
                    new Version(0, 1, 0),
                    null));
        }
    }

    private sealed class FakeWindowsCompatibilityService : IWindowsCompatibilityService
    {
        public WindowsCompatibilityInfo GetCompatibility()
        {
            return new WindowsCompatibilityInfo(
                true,
                new Version(10, 0, 22631),
                "Windows 11",
                "Windows 11 compatibility confirmed.");
        }
    }

    private sealed class FakeEnvironmentScanner : IEnvironmentScanner
    {
        public Task<EnvironmentScanResult> ScanAsync(
            IProgress<EnvironmentScanProgress>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new EnvironmentScanProgress(
                EnvironmentScanStage.Completed,
                7,
                7,
                "스캔이 완료되었습니다."));
            return Task.FromResult(
                new EnvironmentScanResult(
                    null,
                    [],
                    [],
                    [],
                    [],
                    [],
                    [new ScanWarning("Test", "Partial", "Partial test warning.")],
                    new EnvironmentScanSummary(3, 2, 1, 4, 1, 1)));
        }
    }
}
