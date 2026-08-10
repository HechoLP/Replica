using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Replica.Core.Models;
using Replica.Core.Navigation;
using Replica.Core.Scanning;
using Replica.Core.Services;
using Replica.Core.Snapshots;

namespace Replica.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IAppVersionService _appVersion;
    private readonly IDialogService _dialogService;
    private readonly IEnvironmentScanner _environmentScanner;
    private readonly INavigationService _navigationService;
    private readonly IUpdateCheckService _updateCheckService;

    [ObservableProperty]
    private bool _isSnapshotTypePickerVisible;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private bool _isScanSummaryVisible;

    [ObservableProperty]
    private int _scanProgressPercentage;

    [ObservableProperty]
    private string _scanStatus = "스캔 준비";

    [ObservableProperty]
    private string _scanSummaryText = string.Empty;

    public MainViewModel(
        IAppVersionService appVersion,
        IDialogService dialogService,
        ILocalizationService localizationService,
        INavigationService navigationService,
        IUpdateCheckService updateCheckService,
        IWindowsCompatibilityService compatibilityService,
        IEnvironmentScanner environmentScanner,
        DiffViewerViewModel diffViewer,
        RestoreDryRunViewModel restoreDryRun,
        RollbackViewModel rollback,
        RecoveryWizardViewModel? recoveryWizard = null,
        SnapshotHistoryViewModel? snapshotHistory = null)
    {
        _appVersion = appVersion;
        _dialogService = dialogService;
        _environmentScanner = environmentScanner;
        _navigationService = navigationService;
        _updateCheckService = updateCheckService;

        Title = localizationService.GetString("ProductName");
        Tagline = localizationService.GetString("ProductTagline");
        Compatibility = compatibilityService.GetCompatibility();
        DiffViewer = diffViewer;
        RestoreDryRun = restoreDryRun;
        Rollback = rollback;
        RecoveryWizard = recoveryWizard;
        SnapshotHistory = snapshotHistory;
        SnapshotTypes =
        [
            new SnapshotTypeOption(
                SnapshotType.Lightweight,
                "Lightweight Snapshot",
                "프로그램과 설정 인벤토리를 기록합니다."),
            new SnapshotTypeOption(
                SnapshotType.Recovery,
                "Recovery Snapshot",
                "명시적으로 선택한 파일과 복구 설정을 추가합니다."),
            new SnapshotTypeOption(
                SnapshotType.OfflineRecoveryPack,
                "Offline Recovery Pack",
                "선택한 오프라인 설치 자료를 포함할 수 있습니다."),
        ];
    }

    public string Title { get; }

    public string Tagline { get; }

    public string VersionText => $"v{_appVersion.DisplayVersion}";

    public WindowsCompatibilityInfo Compatibility { get; }

    public DiffViewerViewModel DiffViewer { get; }

    public RestoreDryRunViewModel RestoreDryRun { get; }

    public RollbackViewModel Rollback { get; }

    public RecoveryWizardViewModel? RecoveryWizard { get; }

    public SnapshotHistoryViewModel? SnapshotHistory { get; }

    public IReadOnlyList<SnapshotTypeOption> SnapshotTypes { get; }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task ScanCurrentComputerAsync(CancellationToken cancellationToken)
    {
        IsScanning = true;
        IsScanSummaryVisible = false;
        ScanProgressPercentage = 0;
        ScanStatus = "스캔을 시작합니다.";

        Progress<EnvironmentScanProgress> progress = new(update =>
        {
            ScanStatus = update.Status;
            ScanProgressPercentage = update.TotalStages == 0
                ? 0
                : (int)Math.Round(
                    update.CompletedStages * 100d / update.TotalStages,
                    MidpointRounding.AwayFromZero);
        });

        try
        {
            EnvironmentScanResult result = await _environmentScanner
                .ScanAsync(progress, cancellationToken);
            EnvironmentScanSummary summary = result.Summary;
            ScanSummaryText = string.Join(
                System.Environment.NewLine,
                $"프로그램: {summary.ApplicationCount:N0}",
                $"winget 매칭: {summary.WinGetMatchCount:N0}",
                $"미매칭 프로그램: {summary.UnmatchedApplicationCount:N0}",
                $"환경변수: {summary.EnvironmentVariableCount:N0}",
                $"민감 값 제외: {summary.SensitiveExclusionCount:N0}",
                $"경고: {summary.WarningCount:N0}");
            ScanStatus = "읽기 전용 스캔이 완료되었습니다.";
            ScanProgressPercentage = 100;
            IsScanSummaryVisible = true;
            _dialogService.ShowMessage("현재 PC 스캔", ScanSummaryText);
        }
        catch (OperationCanceledException)
        {
            ScanStatus = "스캔이 취소되었습니다. PC는 변경되지 않았습니다.";
        }
        catch (Exception)
        {
            ScanStatus = "스캔을 완료하지 못했습니다. PC는 변경되지 않았습니다.";
            _dialogService.ShowMessage("현재 PC 스캔", ScanStatus);
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private void CreateSnapshot()
    {
        IsSnapshotTypePickerVisible = !IsSnapshotTypePickerVisible;
    }

    [RelayCommand]
    private void CreateRecoverySnapshot()
    {
        ShowPlannedFeature("초기화 복구 Snapshot 만들기");
    }

    [RelayCommand]
    private void OpenSnapshot()
    {
        ShowPlannedFeature("Snapshot 열기");
    }

    [RelayCommand]
    private async Task RestoreAfterResetAsync(CancellationToken cancellationToken)
    {
        if (RecoveryWizard is null)
        {
            ShowPlannedFeature("초기화 후 복구");
            return;
        }

        await RecoveryWizard.BeginAsync(cancellationToken);
    }

    [RelayCommand]
    private async Task ShowSnapshotHistoryAsync(CancellationToken cancellationToken)
    {
        _navigationService.Navigate(NavigationDestination.SnapshotHistory);
        if (SnapshotHistory is null)
        {
            ShowPlannedFeature("Snapshot 기록");
            return;
        }

        await SnapshotHistory.ShowAsync(cancellationToken);
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        UpdateCheckResult result = await _updateCheckService
            .CheckForUpdatesAsync(false, cancellationToken);

        string message = result.Status switch
        {
            UpdateCheckStatus.UpdateAvailable =>
                $"새 버전 {result.LatestRelease?.TagName}을(를) 사용할 수 있습니다.",
            UpdateCheckStatus.UpToDate => "현재 최신 버전을 사용하고 있습니다.",
            _ => "업데이트 확인 기능은 준비 중입니다. 다운로드는 수행되지 않았습니다.",
        };

        _dialogService.ShowMessage("업데이트 확인", message);
    }

    [RelayCommand]
    private void ShowSettings()
    {
        _navigationService.Navigate(NavigationDestination.Settings);
        ShowPlannedFeature("설정");
    }

    [RelayCommand]
    private void ShowAbout()
    {
        _navigationService.Navigate(NavigationDestination.About);
        _dialogService.ShowMessage(
            "Replica 정보",
            $"Replica {VersionText}\n{Tagline}");
    }

    [RelayCommand]
    private void SelectSnapshotType(SnapshotTypeOption? option)
    {
        if (option is null)
        {
            return;
        }

        IsSnapshotTypePickerVisible = false;
        ShowPlannedFeature(option.DisplayName);
    }

    private void ShowPlannedFeature(string featureName)
    {
        _dialogService.ShowMessage(
            featureName,
            $"{featureName} 기능은 구현 예정입니다. 현재 단계에서는 PC를 변경하지 않습니다.");
    }
}
