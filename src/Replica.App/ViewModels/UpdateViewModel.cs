using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Replica.App.Services;
using Replica.Core.Models;
using Replica.Core.Services;
using Replica.Core.Updates;

namespace Replica.App.ViewModels;

public sealed partial class UpdateViewModel : ObservableObject
{
    private readonly IApplicationLifetime applicationLifetime;
    private readonly IUpdateCheckService checks;
    private readonly IUpdateDialogService dialogs;
    private readonly IUpdateDownloadService downloads;
    private readonly IUpdateInstallerService installers;
    private readonly IUpdatePreferenceService preferences;
    private GitHubReleaseDetails? selectedRelease;
    private UpdateDownloadResult? downloadedUpdate;
    private bool updateAvailable;

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    [NotifyCanExecuteChangedFor(nameof(SkipVersionCommand))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private UpdateChannel _selectedChannel = UpdateChannel.Stable;

    [ObservableProperty]
    private string _currentVersion = "-";

    [ObservableProperty]
    private string _latestVersion = "-";

    [ObservableProperty]
    private string _releaseName = "-";

    [ObservableProperty]
    private string _publishedAt = "-";

    [ObservableProperty]
    private string _assetInformation = "-";

    [ObservableProperty]
    private string _checksumInformation = "-";

    [ObservableProperty]
    private string _releaseNotes = string.Empty;

    [ObservableProperty]
    private bool _areReleaseNotesVisible;

    [ObservableProperty]
    private int _progressPercentage;

    [ObservableProperty]
    private string _statusText = "업데이트 확인 준비";

    public UpdateViewModel(
        IUpdateCheckService checks,
        IUpdatePreferenceService preferences,
        IUpdateDownloadService downloads,
        IUpdateInstallerService installers,
        IUpdateDialogService dialogs,
        IApplicationLifetime applicationLifetime)
    {
        this.checks = checks;
        this.preferences = preferences;
        this.downloads = downloads;
        this.installers = installers;
        this.dialogs = dialogs;
        this.applicationLifetime = applicationLifetime;
        Channels = Enum.GetValues<UpdateChannel>();
    }

    public IReadOnlyList<UpdateChannel> Channels { get; }

    public async Task ShowAsync(CancellationToken cancellationToken = default)
    {
        IsVisible = true;
        UpdatePreference preference = await preferences.GetAsync(cancellationToken);
        SelectedChannel = preference.Channel;
        await CheckCoreAsync(cancellationToken);
    }

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckAsync(CancellationToken cancellationToken) => CheckCoreAsync(cancellationToken);

    [RelayCommand]
    private void ShowReleaseNotes() => AreReleaseNotesVisible = !AreReleaseNotesVisible;

    [RelayCommand]
    private void Later()
    {
        IsVisible = false;
        StatusText = "업데이트를 나중에 다시 확인할 수 있습니다.";
    }

    [RelayCommand(CanExecute = nameof(CanSkip))]
    private async Task SkipVersionAsync(CancellationToken cancellationToken)
    {
        if (selectedRelease is null)
        {
            return;
        }

        await RunAsync(async () =>
        {
            await preferences.SaveAsync(
                new UpdatePreference(SelectedChannel, selectedRelease.TagName),
                cancellationToken);
            updateAvailable = false;
            StatusText = $"{selectedRelease.TagName} 버전을 건너뜁니다. 더 새로운 버전은 다시 제안됩니다.";
            DownloadCommand.NotifyCanExecuteChanged();
        });
    }

    [RelayCommand(CanExecute = nameof(CanDownload), IncludeCancelCommand = true)]
    private async Task DownloadAsync(CancellationToken cancellationToken)
    {
        if (selectedRelease is null || !dialogs.ConfirmDownload(selectedRelease))
        {
            return;
        }

        await RunAsync(async () =>
        {
            Progress<UpdateDownloadProgress> progress = new(update =>
            {
                ProgressPercentage = update.TotalBytes <= 0
                    ? 0
                    : (int)Math.Clamp(update.ProcessedBytes * 100 / update.TotalBytes, 0, 100);
                StatusText = update.Stage switch
                {
                    UpdateDownloadStage.Preparing => "업데이트 다운로드 준비 중…",
                    UpdateDownloadStage.DownloadingChecksum => "SHA-256 Asset 다운로드 중…",
                    UpdateDownloadStage.DownloadingInstaller => "ReplicaSetup.exe 다운로드 중…",
                    UpdateDownloadStage.Verifying => "Installer SHA-256 검증 중…",
                    _ => "업데이트 다운로드 완료",
                };
            });
            downloadedUpdate = await downloads.DownloadAsync(
                new UpdateDownloadRequest(selectedRelease, UserApproved: true),
                progress,
                cancellationToken);
            ProgressPercentage = 100;
            ChecksumInformation = downloadedUpdate.ChecksumStatus == UpdateChecksumStatus.Verified
                ? "SHA-256 검증 완료"
                : "Checksum 없음 — 설치 전 계산된 Hash를 다시 확인합니다.";
            StatusText = "ReplicaSetup.exe를 Temp 폴더에 안전하게 다운로드했습니다. 자동 실행하지 않았습니다.";
            InstallCommand.NotifyCanExecuteChanged();
        });
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync(CancellationToken cancellationToken)
    {
        if (downloadedUpdate is null || !dialogs.ConfirmInstall(downloadedUpdate))
        {
            return;
        }

        await RunAsync(async () =>
        {
            UpdateInstallerLaunchResult result = await installers.LaunchAsync(
                new UpdateInstallerLaunchRequest(downloadedUpdate, UserApproved: true),
                cancellationToken);
            if (result.Started)
            {
                applicationLifetime.Shutdown();
            }
        });
    }

    private async Task CheckCoreAsync(CancellationToken cancellationToken)
    {
        await RunAsync(async () =>
        {
            UpdatePreference existing = await preferences.GetAsync(cancellationToken);
            await preferences.SaveAsync(
                new UpdatePreference(SelectedChannel, existing.SkippedVersionTag),
                cancellationToken);
            UpdateCheckResult result = await checks.CheckForUpdatesAsync(
                SelectedChannel,
                cancellationToken);
            CurrentVersion = SemanticVersion.FromVersion(result.CurrentVersion).ToString();
            selectedRelease = result.LatestRelease;
            updateAvailable = result.Status == UpdateCheckStatus.UpdateAvailable;
            downloadedUpdate = null;
            InstallCommand.NotifyCanExecuteChanged();
            if (selectedRelease is not null)
            {
                LatestVersion = selectedRelease.Version.ToString();
                ReleaseName = selectedRelease.ReleaseName;
                PublishedAt = selectedRelease.PublishedAtUtc.ToLocalTime().ToString("g");
                ReleaseNotes = selectedRelease.ReleaseNotes;
                GitHubReleaseAssetInfo? installer = selectedRelease.Assets.FirstOrDefault(asset =>
                    asset.Name.Equals("ReplicaSetup.exe", StringComparison.Ordinal));
                AssetInformation = installer is null
                    ? "ReplicaSetup.exe Asset 없음"
                    : $"{installer.Name} · {installer.Size:N0} B · HTTPS";
                ChecksumInformation = selectedRelease.HasSha256Asset
                    ? "SHA-256 Asset 있음"
                    : "SHA-256 Asset 없음";
            }
            else
            {
                ClearRelease();
            }

            StatusText = FormatStatus(result);
            DownloadCommand.NotifyCanExecuteChanged();
            SkipVersionCommand.NotifyCanExecuteChanged();
        });
    }

    private async Task RunAsync(Func<Task> operation)
    {
        IsBusy = true;
        try
        {
            await operation();
        }
        catch (OperationCanceledException)
        {
            StatusText = "업데이트 작업을 취소했습니다. 불완전한 다운로드는 실행되지 않습니다.";
        }
        catch (UpdateDownloadException exception)
        {
            StatusText = $"업데이트 작업을 완료하지 못했습니다. ({exception.Code})";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or HttpRequestException)
        {
            StatusText = $"업데이트 작업을 완료하지 못했습니다. ({exception.GetType().Name})";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanCheck() => !IsBusy;

    private bool CanDownload() => !IsBusy && updateAvailable && selectedRelease is not null && downloadedUpdate is null;

    private bool CanSkip() => !IsBusy && updateAvailable && selectedRelease is not null;

    private bool CanInstall() => !IsBusy && downloadedUpdate is not null;

    private void ClearRelease()
    {
        LatestVersion = "-";
        ReleaseName = "-";
        PublishedAt = "-";
        AssetInformation = "-";
        ChecksumInformation = "-";
        ReleaseNotes = string.Empty;
        AreReleaseNotesVisible = false;
    }

    private static string FormatStatus(UpdateCheckResult result) => result.Status switch
    {
        UpdateCheckStatus.UpdateAvailable => "Replica 새 버전이 있습니다.",
        UpdateCheckStatus.UpToDate => "현재 채널의 최신 버전을 사용 중입니다.",
        UpdateCheckStatus.Skipped => "이 버전은 사용자가 건너뛰도록 설정했습니다.",
        UpdateCheckStatus.RateLimited => result.RetryAtUtc is DateTimeOffset retry
            ? $"GitHub API Rate Limit입니다. {retry.ToLocalTime():g} 이후 다시 확인하세요."
            : "GitHub API Rate Limit입니다. 잠시 후 다시 확인하세요.",
        UpdateCheckStatus.NetworkUnavailable => "네트워크에 연결할 수 없습니다.",
        UpdateCheckStatus.TimedOut => "GitHub Release 조회 시간이 초과되었습니다.",
        UpdateCheckStatus.InvalidResponse => "GitHub가 잘못된 Release 정보를 반환했습니다.",
        UpdateCheckStatus.AssetMissing => "Release에 올바른 ReplicaSetup.exe Asset이 없습니다.",
        _ => "현재 업데이트 정보를 사용할 수 없습니다.",
    };
}
