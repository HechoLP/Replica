using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Replica.App.Services;
using Replica.Core.Diffing;
using Replica.Core.Models;
using Replica.Core.Navigation;
using Replica.Core.Planning;
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
    private readonly IEnvironmentDiffEngine? _diffEngine;
    private readonly IRecoveryDialogService? _snapshotDialogs;
    private readonly ISnapshotComparisonService? _snapshotComparisonService;
    private readonly IRestorePlanner? _restorePlanner;
    private readonly IUpdateCheckService _updateCheckService;
    private readonly ReplicaUiSession _session;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPageIndex))]
    private NavigationDestination _currentDestination = NavigationDestination.Home;

    [ObservableProperty]
    private string _currentPageTitle = "시작";

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
        IReplicaPathProvider? pathProvider = null,
        IRecoveryDialogService? snapshotDialogs = null,
        ISnapshotComparisonService? snapshotComparisonService = null,
        IEnvironmentDiffEngine? diffEngine = null,
        IRestorePlanner? restorePlanner = null)
    {
        _appVersion = appVersion;
        _dialogService = dialogService;
        _environmentScanner = environmentScanner;
        _navigationService = navigationService;
        _snapshotDialogs = snapshotDialogs;
        _snapshotComparisonService = snapshotComparisonService;
        _diffEngine = diffEngine;
        _restorePlanner = restorePlanner;
        _updateCheckService = updateCheckService;
        _session = session ?? new ReplicaUiSession();

        Title = localizationService.GetString("ProductName");
        Tagline = localizationService.GetString("ProductTagline");
        Compatibility = compatibilityService.GetCompatibility();
        DiffViewer = diffViewer;
        DiffViewer.SelectionChanged += InvalidateRestoreApproval;
        RestoreDryRun = restoreDryRun;
        RestoreDryRun.ModeSelectionRequested += RebuildRestorePlan;
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
                "선택 파일과 서명이 검증된 오프라인 설치 자료를 함께 보관합니다."),
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
        catch (Exception exception) when (exception is not OutOfMemoryException)
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
    private async Task OpenSnapshotAsync(CancellationToken cancellationToken)
    {
        if (_snapshotDialogs is null || _snapshotComparisonService is null || _restorePlanner is null)
        {
            ShowPlannedFeature("Snapshot 열기");
            return;
        }

        _session.ClearComparison();
        RestoreDryRun.Invalidate("새 Snapshot 비교가 완료될 때까지 이전 Restore Plan은 실행할 수 없습니다.");
        DiffViewer.Clear("Snapshot을 선택하면 무결성을 확인하고 현재 PC와 비교합니다.");

        string? path = _snapshotDialogs.SelectRecoverySnapshot();
        if (path is null)
        {
            return;
        }

        _navigationService.Navigate(NavigationDestination.Comparison);
        DiffViewer.StatusText = "Snapshot 무결성을 확인하고 현재 PC를 읽기 전용으로 스캔하는 중입니다.";
        try
        {
            SnapshotEnvironmentComparisonResult comparison;
            try
            {
                comparison = await _snapshotComparisonService.CompareAsync(
                    path,
                    ReadOnlyMemory<char>.Empty,
                    DiffRestoreMode.Safe,
                    cancellationToken);
            }
            catch (ReplicaSnapshotDecryptionException)
            {
                char[]? password = _snapshotDialogs.RequestPassword(
                    "암호화 Snapshot",
                    "Snapshot 비밀번호를 입력하세요. 비밀번호는 저장하거나 기록하지 않습니다.");
                if (password is null)
                {
                    DiffViewer.StatusText = "비밀번호 입력을 취소했습니다. PC는 변경되지 않았습니다.";
                    return;
                }

                try
                {
                    comparison = await _snapshotComparisonService.CompareAsync(
                        path,
                        password,
                        DiffRestoreMode.Safe,
                        cancellationToken);
                }
                finally
                {
                    Array.Clear(password);
                }
            }

            _session.LatestSnapshotPath = path;
            _session.LatestScan = comparison.CurrentEnvironment;
            _session.LatestSnapshotEnvironment = comparison.SnapshotEnvironment;
            _session.LatestCurrentEnvironment = comparison.CurrentEnvironmentState;
            _session.LatestDiff = comparison.Diff;
            DiffViewer.Display(comparison.Diff);
            BuildRestorePlan(DiffRestoreMode.Safe, navigate: false);
        }
        catch (OperationCanceledException)
        {
            DiffViewer.StatusText = "Snapshot 비교를 취소했습니다. PC는 변경되지 않았습니다.";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            DiffViewer.StatusText = "Snapshot을 안전하게 열거나 비교하지 못했습니다. PC는 변경되지 않았습니다.";
            _snapshotDialogs.ShowError("Snapshot 열기", DiffViewer.StatusText);
        }
    }

    [RelayCommand]
    private void BuildRestorePlan() => BuildRestorePlan(RestoreDryRun.SelectedMode, navigate: true);

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
            _ => "업데이트 정보를 확인하지 못했습니다. 네트워크 상태를 확인하고 다시 시도하세요.",
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

    private void RebuildRestorePlan(DiffRestoreMode mode) => BuildRestorePlan(mode, navigate: false);

    private void InvalidateRestoreApproval()
    {
        if (RestoreDryRun.CanReview || RestoreDryRun.ReviewedPlan is not null)
        {
            RestoreDryRun.Invalidate("Diff 선택이 변경되어 이전 승인이 취소되었습니다. Restore Plan을 다시 만드세요.");
        }
    }

    private void BuildRestorePlan(DiffRestoreMode mode, bool navigate)
    {
        if (_restorePlanner is null || _session.LatestDiff is null)
        {
            RestoreDryRun.StatusText = "먼저 Snapshot을 열고 현재 PC와 비교하세요.";
            return;
        }

        try
        {
            IReadOnlySet<DiffSelectionKey> selection = DiffViewer.GetSelectedReferences();
            if (_diffEngine is not null &&
                _session.LatestSnapshotEnvironment is not null &&
                _session.LatestCurrentEnvironment is not null)
            {
                EnvironmentDiffResult refreshed = _diffEngine.Compare(
                    _session.LatestSnapshotEnvironment,
                    _session.LatestCurrentEnvironment,
                    mode);
                _session.LatestDiff = refreshed;
                DiffViewer.Display(refreshed, selection);
            }

            EnvironmentDiffResult selected = DiffViewer.GetSelectedDiff(mode);
            RestorePlan plan = _restorePlanner.CreatePlan(
                selected,
                new RestorePlanningOptions([], SupportedInteractiveActionTypes));
            RestoreDryRun.Display(plan);
            if (navigate)
            {
                _navigationService.Navigate(NavigationDestination.RestorePlan);
            }
        }
        catch (Exception)
        {
            RestoreDryRun.StatusText = "선택한 차이로 안전한 Restore Plan을 만들지 못했습니다.";
        }
    }

    private static IReadOnlySet<RestoreActionType> SupportedInteractiveActionTypes { get; } =
        new HashSet<RestoreActionType>
        {
            RestoreActionType.InstallPackage,
            RestoreActionType.UpdatePackage,
            RestoreActionType.SetUserEnvironmentVariable,
            RestoreActionType.SetMachineEnvironmentVariable,
            RestoreActionType.AddPathEntry,
            RestoreActionType.Validate,
        };

    private void UpdatePageMetadata(NavigationDestination destination)
    {
        (CurrentPageTitle, CurrentPageDescription) = destination switch
        {
            NavigationDestination.Home => ("시작", "현재 PC 확인부터 Snapshot 생성, 비교와 복원까지 순서대로 진행합니다."),
            NavigationDestination.Scan => ("현재 PC 확인", "프로그램과 설정 정보를 변경 없이 읽어 Snapshot 준비 상태를 확인합니다."),
            NavigationDestination.SnapshotBuilder => ("Snapshot 만들기", "포함 범위와 민감 데이터 제외, 암호화, 저장 위치를 확인한 뒤 파일을 만듭니다."),
            NavigationDestination.Comparison => ("Snapshot 비교 요약", "Snapshot과 현재 PC가 얼마나 같은지, 어떤 범주가 다른지 확인합니다."),
            NavigationDestination.DiffViewer => ("복원할 항목 고르기", "현재 값과 Snapshot 값을 비교하고 복원할 항목만 선택합니다."),
            NavigationDestination.RestorePlan => ("복원 계획 검토", "실제로 바뀔 항목과 권한·재부팅·롤백 가능 여부를 실행 전에 확인합니다."),
            NavigationDestination.RecoveryWizard => ("초기화 후 복구", "Recovery Snapshot 검증부터 최종 확인과 수동 작업까지 단계별로 진행합니다."),
            NavigationDestination.Execution => ("선택 작업 복원", "승인한 작업만 실행하며 현재 단계와 안전한 취소 상태를 표시합니다."),
            NavigationDestination.Result => ("복원 결과", "완료·실패·재부팅·수동 작업과 롤백 가능 여부를 확인합니다."),
            NavigationDestination.SnapshotHistory => ("Snapshot 및 복원 기록", "Snapshot 비교와 복원·롤백 기록을 관리합니다."),
            NavigationDestination.Settings => ("설정", "기본 Snapshot 폴더, 테마와 업데이트 채널을 관리합니다."),
            NavigationDestination.About => ("Replica 정보", "버전, 공식 GitHub 경로, 라이선스와 로그 위치를 확인합니다."),
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
