using System.Globalization;
using Replica.App.Services;
using Replica.App.ViewModels;
using Replica.Core.Diffing;
using Replica.Core.Matching;
using Replica.Core.Models;
using Replica.Core.Navigation;
using Replica.Core.Planning;
using Replica.Core.Rollback;
using Replica.Core.Scanning;
using Replica.Core.Services;
using Replica.Core.Updates;

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
    public async Task OpenSnapshotCommand_ValidatesScansComparesAndBuildsDryRun()
    {
        FakeRecoveryDialogs dialogs = new();
        MainViewModel viewModel = CreateViewModel(
            out FakeDialogService dialog,
            out FakeNavigationService navigation,
            snapshotDialogs: dialogs,
            snapshotComparisonService: new FakeSnapshotComparisonService(),
            restorePlanner: new RestorePlanner());

        await viewModel.OpenSnapshotCommand.ExecuteAsync(null);

        Assert.Equal(NavigationDestination.Comparison, navigation.CurrentDestination);
        Assert.Single(viewModel.DiffViewer.Items);
        Assert.Contains("1개 비교 항목", viewModel.DiffViewer.StatusText, StringComparison.Ordinal);
        Assert.NotEmpty(viewModel.RestoreDryRun.Actions);
        Assert.DoesNotContain("구현 예정", dialog.LastMessage, StringComparison.Ordinal);
        Assert.Null(dialogs.LastError);
    }

    [Fact]
    public async Task RestorePlan_UsesReviewedDiffSelectionAndRegeneratesForMode()
    {
        MainViewModel viewModel = CreateViewModel(
            out _,
            out FakeNavigationService navigation,
            snapshotDialogs: new FakeRecoveryDialogs(),
            snapshotComparisonService: new FakeSnapshotComparisonService(),
            restorePlanner: new RestorePlanner());
        await viewModel.OpenSnapshotCommand.ExecuteAsync(null);
        Assert.NotEmpty(viewModel.RestoreDryRun.Actions);

        viewModel.DiffViewer.Items[0].IsSelected = false;
        viewModel.BuildRestorePlanCommand.Execute(null);

        Assert.Equal(NavigationDestination.RestorePlan, navigation.CurrentDestination);
        Assert.Empty(viewModel.RestoreDryRun.Actions);

        viewModel.DiffViewer.Items[0].IsSelected = true;
        viewModel.RestoreDryRun.SelectModeCommand.Execute(DiffRestoreMode.Recommended);
        Assert.Equal(DiffRestoreMode.Recommended, viewModel.RestoreDryRun.SelectedMode);
        Assert.NotEmpty(viewModel.RestoreDryRun.Actions);
    }

    [Fact]
    public async Task DiffSelectionChange_InvalidatesPreviouslyApprovedPlan()
    {
        MainViewModel viewModel = CreateViewModel(
            out _,
            out _,
            snapshotDialogs: new FakeRecoveryDialogs(),
            snapshotComparisonService: new FakeSnapshotComparisonService(),
            restorePlanner: new RestorePlanner());
        await viewModel.OpenSnapshotCommand.ExecuteAsync(null);
        viewModel.RestoreDryRun.ConfirmPlanCommand.Execute(null);
        Assert.True(viewModel.RestoreDryRun.ReviewedPlan?.IsApproved);

        viewModel.DiffViewer.Items[0].IsSelected = false;

        Assert.Null(viewModel.RestoreDryRun.ReviewedPlan);
        Assert.Empty(viewModel.RestoreDryRun.Actions);
        Assert.Contains("승인이 취소", viewModel.RestoreDryRun.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedNewSnapshotOpen_CannotLeavePreviousApprovedPlanExecutable()
    {
        FailOnSecondComparisonService comparison = new();
        FakeRecoveryDialogs dialogs = new();
        MainViewModel viewModel = CreateViewModel(
            out _,
            out _,
            snapshotDialogs: dialogs,
            snapshotComparisonService: comparison,
            restorePlanner: new RestorePlanner());
        await viewModel.OpenSnapshotCommand.ExecuteAsync(null);
        viewModel.RestoreDryRun.ConfirmPlanCommand.Execute(null);
        Assert.True(viewModel.RestoreDryRun.ReviewedPlan?.IsApproved);

        await viewModel.OpenSnapshotCommand.ExecuteAsync(null);

        Assert.Null(viewModel.RestoreDryRun.ReviewedPlan);
        Assert.Empty(viewModel.RestoreDryRun.Actions);
        Assert.Empty(viewModel.DiffViewer.Items);
        Assert.NotNull(dialogs.LastError);
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
    [Trait("Category", "Performance")]
    public async Task ScanCurrentComputerCommand_DoesNotBlockWhileScannerIsAwaitingIo()
    {
        BlockingEnvironmentScanner scanner = new();
        MainViewModel viewModel = CreateViewModel(out _, out _, environmentScanner: scanner);

        Task execution = viewModel.ScanCurrentComputerCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsScanning);
        Assert.False(execution.IsCompleted);
        scanner.Complete();
        await execution;
        Assert.False(viewModel.IsScanning);
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
        IUpdateCheckService? updateService = null,
        IEnvironmentScanner? environmentScanner = null,
        IRecoveryDialogService? snapshotDialogs = null,
        ISnapshotComparisonService? snapshotComparisonService = null,
        IRestorePlanner? restorePlanner = null)
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
            environmentScanner ?? new FakeEnvironmentScanner(),
            new DiffViewerViewModel(),
            new RestoreDryRunViewModel(),
            new RollbackViewModel(new FakeRollbackService()),
            snapshotDialogs: snapshotDialogs,
            snapshotComparisonService: snapshotComparisonService,
            restorePlanner: restorePlanner);
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

        public Task<UpdateCheckResult> CheckForUpdatesAsync(
            UpdateChannel channel,
            CancellationToken cancellationToken) => CheckForUpdatesAsync(
                channel != UpdateChannel.Stable,
                cancellationToken);
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

    private sealed class BlockingEnvironmentScanner : IEnvironmentScanner
    {
        private readonly TaskCompletionSource<EnvironmentScanResult> completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<EnvironmentScanResult> ScanAsync(
            IProgress<EnvironmentScanProgress>? progress,
            CancellationToken cancellationToken) => completion.Task.WaitAsync(cancellationToken);

        public void Complete()
        {
            completion.SetResult(new EnvironmentScanResult(
                null,
                [],
                [],
                [],
                [],
                [],
                [],
                new EnvironmentScanSummary(0, 0, 0, 0, 0, 0)));
        }
    }

    private sealed class FakeRecoveryDialogs : IRecoveryDialogService
    {
        public string? LastError { get; private set; }

        public string? SelectRecoverySnapshot() => "C:\\Fixture\\environment.replica";

        public char[]? RequestPassword(string title, string message) => null;

        public bool Confirm(string title, string message) => false;

        public void ShowError(string title, string message) => LastError = message;
    }

    private sealed class FakeSnapshotComparisonService : ISnapshotComparisonService
    {
        public Task<SnapshotEnvironmentComparisonResult> CompareAsync(
            string snapshotPath,
            ReadOnlyMemory<char> password,
            DiffRestoreMode mode,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DiffItem missing = new(
                DiffType.Missing,
                DiffArea.Applications,
                "GIT.GIT",
                "Git",
                "2.50.0",
                null,
                ApplicationMatchConfidence.Exact,
                true,
                false,
                false,
                DiffRiskLevel.Low,
                false,
                false,
                0,
                "MissingSourceApplication");
            EnvironmentDiffResult diff = new(
                mode,
                [missing],
                new EnvironmentSimilarityScore(70, 100, 0, 0, 0, []));
            return Task.FromResult(new SnapshotEnvironmentComparisonResult(
                null!,
                CreateScanResult(),
                DiffEnvironmentState.Empty,
                DiffEnvironmentState.Empty,
                diff));
        }
    }

    private sealed class FailOnSecondComparisonService : ISnapshotComparisonService
    {
        private readonly FakeSnapshotComparisonService successful = new();
        private int callCount;

        public Task<SnapshotEnvironmentComparisonResult> CompareAsync(
            string snapshotPath,
            ReadOnlyMemory<char> password,
            DiffRestoreMode mode,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref callCount) > 1)
            {
                throw new InvalidDataException("Synthetic invalid Snapshot.");
            }

            return successful.CompareAsync(snapshotPath, password, mode, cancellationToken);
        }
    }

    private static EnvironmentScanResult CreateScanResult() => new(
        null,
        [],
        [],
        [],
        [],
        [],
        [],
        new EnvironmentScanSummary(0, 0, 0, 0, 0, 0));

    private sealed class FakeRollbackService : IRollbackService
    {
        public Task<IReadOnlyList<RollbackSessionSummary>> GetRecentSessionsAsync(
            int maximumCount,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<RollbackSessionSummary>>([]);

        public Task<RollbackPlan> CreatePlanAsync(
            string sessionId,
            IReadOnlyCollection<string>? selectedActionIds,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RollbackExecutionResult> ExecuteAsync(
            RollbackPlan approvedPlan,
            IProgress<RollbackProgress>? progress,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
