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
    private readonly ReplicaUiSession _session;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPageIndex))]
    private NavigationDestination _currentDestination = NavigationDestination.Home;

    [ObservableProperty]
    private string _currentPageTitle = "Home";

    [ObservableProperty]
    private string _currentPageDescription = "Windows 환경을 스캔하고 안전한 Snapshot 및 복구 작업을 시작합니다.";

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

    [ObservableProperty]
    private string _scanCurrentStage = "대기";

    [ObservableProperty]
    private IReadOnlyList<ScanFoundItemRowViewModel> _scanFoundItems = [];

    [ObservableProperty]
    private IReadOnlyList<ScanWarningRowViewModel> _scanWarnings = [];

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
        SnapshotHistoryViewModel? snapshotHistory = null,
        PortableSnapshotViewModel? portableSnapshot = null,
        UpdateViewModel? update = null,
        ReplicaUiSession? session = null,
        SnapshotBuilderViewModel? snapshotBuilder = null,
        SettingsViewModel? settings = null,
        RestoreExecutionViewModel? execution = null,
        RestoreResultViewModel? result = null,
        IReplicaPathProvider? pathProvider = null)
    {
        _appVersion = appVersion;
        _dialogService = dialogService;
        _environmentScanner = environmentScanner;
        _navigationService = navigationService;
        _updateCheckService = updateCheckService;
        _session = session ?? new ReplicaUiSession();

        Title = localizationService.GetString("ProductName");
        Tagline = localizationService.GetString("ProductTagline");
        Compatibility = compatibilityService.GetCompatibility();
        DiffViewer = diffViewer;
        RestoreDryRun = restoreDryRun;
        Rollback = rollback;
        RecoveryWizard = recoveryWizard;
        SnapshotHistory = snapshotHistory;
        PortableSnapshot = portableSnapshot;
        Update = update;
        SnapshotBuilder = snapshotBuilder;
        Settings = settings;
        Execution = execution;
        Result = result ?? execution?.Result;
        LogsDirectory = pathProvider?.LogsDirectory ?? "%LOCALAPPDATA%\\Replica\\Logs";
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

        CurrentDestination = navigationService.CurrentDestination;
        UpdatePageMetadata(CurrentDestination);
        navigationService.Navigated += (_, args) =>
        {
            CurrentDestination = args.Destination;
            UpdatePageMetadata(args.Destination);
        };
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

    public PortableSnapshotViewModel? PortableSnapshot { get; }

    public UpdateViewModel? Update { get; }

    public SnapshotBuilderViewModel? SnapshotBuilder { get; }

    public SettingsViewModel? Settings { get; }

    public RestoreExecutionViewModel? Execution { get; }

    public RestoreResultViewModel? Result { get; }

    public IReadOnlyList<SnapshotTypeOption> SnapshotTypes { get; }

    public int CurrentPageIndex => (int)CurrentDestination;

    public string RepositoryUrl => "https://github.com/HechoLP/Replica";

    public string ReleasesUrl => "https://github.com/HechoLP/Replica/releases";

    public string LicenseText => "저장소의 LICENSE 및 라이선스 결정 문서를 확인하세요.";

    public string LogsDirectory { get; }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task ScanCurrentComputerAsync(CancellationToken cancellationToken)
    {
        _navigationService.Navigate(NavigationDestination.Scan);
        IsScanning = true;
        IsScanSummaryVisible = false;
        ScanProgressPercentage = 0;
        ScanStatus = "스캔을 시작합니다.";
        ScanCurrentStage = "Windows 정보";
        ScanFoundItems = [];
        ScanWarnings = [];

        Progress<EnvironmentScanProgress> progress = new(update =>
        {
            ScanStatus = update.Status;
            ScanCurrentStage = GetScanStageName(update.Stage);
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
            _session.LatestScan = result;
            SnapshotBuilder?.Configure(SnapshotBuilder.SnapshotType);
            ScanFoundItems = CreateScanFoundItems(result);
            ScanWarnings = result.Warnings.Select(warning => new ScanWarningRowViewModel(
                warning.Provider,
                warning.Code,
                warning.Message)).ToArray();
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
        _navigationService.Navigate(NavigationDestination.SnapshotBuilder);
    }

    [RelayCommand]
    private void CreateLightweightSnapshot() => StartSnapshotBuilder(SnapshotType.Lightweight);

    [RelayCommand]
    private void CreateRecoverySnapshot() => StartSnapshotBuilder(SnapshotType.Recovery);

    [RelayCommand]
    private void CreateOfflineRecoveryPack() => StartSnapshotBuilder(SnapshotType.OfflineRecoveryPack);

    [RelayCommand]
    private async Task ShowPortableExportAsync(CancellationToken cancellationToken)
    {
        if (PortableSnapshot is null)
        {
            ShowPlannedFeature("Recovery Snapshot 휴대용 내보내기");
            return;
        }

        await PortableSnapshot.ShowAsync(cancellationToken);
    }

    [RelayCommand]
    private void OpenSnapshot()
    {
        _navigationService.Navigate(NavigationDestination.Comparison);
        ShowPlannedFeature("Snapshot 열기");
    }

    [RelayCommand]
    private async Task RestoreAfterResetAsync(CancellationToken cancellationToken)
    {
        _navigationService.Navigate(NavigationDestination.RecoveryWizard);
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
        _navigationService.Navigate(NavigationDestination.Update);
        if (Update is not null)
        {
            await Update.ShowAsync(cancellationToken);
            return;
        }

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
    private async Task ShowSettingsAsync(CancellationToken cancellationToken)
    {
        _navigationService.Navigate(NavigationDestination.Settings);
        if (Settings is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Settings.LoadCommand.ExecuteAsync(null);
        }
    }

    [RelayCommand]
    private void ShowAbout()
    {
        _navigationService.Navigate(NavigationDestination.About);
    }

    [RelayCommand]
    private void Navigate(NavigationDestination destination)
    {
        if (!Enum.IsDefined(destination))
        {
            return;
        }

        _navigationService.Navigate(destination);
        if (destination == NavigationDestination.Execution)
        {
            Execution?.Prepare();
        }
    }

    [RelayCommand]
    private void SelectSnapshotType(SnapshotTypeOption? option)
    {
        if (option is null)
        {
            return;
        }

        IsSnapshotTypePickerVisible = false;
        StartSnapshotBuilder(option.Type);
    }

    private void StartSnapshotBuilder(SnapshotType type)
    {
        SnapshotBuilder?.Configure(type);
        IsSnapshotTypePickerVisible = false;
        _navigationService.Navigate(NavigationDestination.SnapshotBuilder);
    }

    private void UpdatePageMetadata(NavigationDestination destination)
    {
        (CurrentPageTitle, CurrentPageDescription) = destination switch
        {
            NavigationDestination.Home => ("Home", "Windows 환경을 스캔하고 안전한 Snapshot 및 복구 작업을 시작합니다."),
            NavigationDestination.Scan => ("현재 PC 스캔", "Windows 정보와 프로그램·환경변수·글꼴·Built-in Plugin을 읽기 전용으로 확인합니다."),
            NavigationDestination.SnapshotBuilder => ("Snapshot Builder", "포함 범위와 민감 제외, 암호화, 저장 위치를 검토한 뒤 Snapshot을 만듭니다."),
            NavigationDestination.Comparison => ("Comparison", "Snapshot과 현재 PC의 전체 일치율과 차이 범주를 비교합니다."),
            NavigationDestination.DiffViewer => ("Diff Viewer", "검색·필터·위험도와 원본·현재·목표 값을 검토하고 복원 대상을 선택합니다."),
            NavigationDestination.RestorePlan => ("Restore Plan", "Safe·Recommended·Exact 모드의 Dry Run을 최종 승인 전에 검토합니다."),
            NavigationDestination.RecoveryWizard => ("초기화 후 복구", "Recovery Snapshot 검증부터 최종 확인과 수동 작업까지 단계별로 진행합니다."),
            NavigationDestination.Execution => ("Execution", "명시적으로 승인된 Restore Plan만 진행률과 결과를 표시하며 실행합니다."),
            NavigationDestination.Result => ("Result", "복원 결과·재부팅·수동 작업과 Rollback 필요 여부를 확인합니다."),
            NavigationDestination.SnapshotHistory => ("History", "Snapshot 비교와 복원·Rollback 기록을 관리합니다."),
            NavigationDestination.Settings => ("Settings", "언어·Theme·기본 폴더·업데이트와 개인정보 설정을 관리합니다."),
            NavigationDestination.About => ("About", "Replica 버전, 공식 GitHub 경로, 라이선스와 로그 위치를 확인합니다."),
            _ => ("업데이트", "GitHub Releases에서 선택한 Channel의 업데이트를 안전하게 확인합니다."),
        };
    }

    private static IReadOnlyList<ScanFoundItemRowViewModel> CreateScanFoundItems(EnvironmentScanResult result) =>
    [
        new("프로그램", result.Summary.ApplicationCount, $"winget 매칭 {result.Summary.WinGetMatchCount:N0}"),
        new("Store 앱", result.Applications.Count(application =>
            application.Source.Contains("MSIX", StringComparison.OrdinalIgnoreCase)), "Framework는 별도 분류"),
        new("환경변수", result.Summary.EnvironmentVariableCount, $"민감 제외 {result.Summary.SensitiveExclusionCount:N0}"),
        new("PATH", result.PathEntries.Count, $"중복 {result.PathEntries.Count(path => path.IsDuplicate):N0}"),
        new("글꼴", result.Fonts.Count, "파일 자동 포함 안 함"),
        new("Built-in Plugin", result.BuiltInPluginIds.Count, "공식 Plugin"),
    ];

    private static string GetScanStageName(EnvironmentScanStage stage) => stage switch
    {
        EnvironmentScanStage.WindowsInformation => "Windows 정보",
        EnvironmentScanStage.Applications => "프로그램",
        EnvironmentScanStage.StoreApplications => "Store 앱",
        EnvironmentScanStage.EnvironmentVariables => "환경변수",
        EnvironmentScanStage.Path => "PATH",
        EnvironmentScanStage.Fonts => "글꼴",
        EnvironmentScanStage.BuiltInPlugins => "Built-in Plugin",
        _ => "완료",
    };

    private void ShowPlannedFeature(string featureName)
    {
        _dialogService.ShowMessage(
            featureName,
            $"{featureName} 기능은 구현 예정입니다. 현재 단계에서는 PC를 변경하지 않습니다.");
    }
}

public sealed record ScanFoundItemRowViewModel(string Category, int Count, string Detail);

public sealed record ScanWarningRowViewModel(string Provider, string Code, string Message);
