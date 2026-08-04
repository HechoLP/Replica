using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Replica.Core.Models;
using Replica.Core.Navigation;
using Replica.Core.Services;
using Replica.Core.Snapshots;

namespace Replica.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IAppVersionService _appVersion;
    private readonly IDialogService _dialogService;
    private readonly INavigationService _navigationService;
    private readonly IUpdateCheckService _updateCheckService;

    [ObservableProperty]
    private bool _isSnapshotTypePickerVisible;

    public MainViewModel(
        IAppVersionService appVersion,
        IDialogService dialogService,
        ILocalizationService localizationService,
        INavigationService navigationService,
        IUpdateCheckService updateCheckService,
        IWindowsCompatibilityService compatibilityService)
    {
        _appVersion = appVersion;
        _dialogService = dialogService;
        _navigationService = navigationService;
        _updateCheckService = updateCheckService;

        Title = localizationService.GetString("ProductName");
        Tagline = localizationService.GetString("ProductTagline");
        Compatibility = compatibilityService.GetCompatibility();
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

    public IReadOnlyList<SnapshotTypeOption> SnapshotTypes { get; }

    [RelayCommand]
    private void ScanCurrentComputer()
    {
        ShowPlannedFeature("현재 PC 스캔");
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
    private void RestoreAfterReset()
    {
        ShowPlannedFeature("초기화 후 복구");
    }

    [RelayCommand]
    private void ShowSnapshotHistory()
    {
        _navigationService.Navigate(NavigationDestination.SnapshotHistory);
        ShowPlannedFeature("Snapshot 기록");
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
