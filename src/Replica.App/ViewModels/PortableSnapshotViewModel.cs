using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Replica.App.Services;
using Replica.Core.Portable;
using Replica.Core.Services;
using Replica.Core.Snapshots;

namespace Replica.App.ViewModels;

public sealed partial class PortableSnapshotViewModel : ObservableObject
{
    private readonly IPortableSnapshotDialogService dialogs;
    private readonly IPortableSnapshotExporter exporter;
    private readonly IReplicaInstallerDownloadService installerDownloads;
    private readonly IPreResetChecklistService checklistService;
    private readonly IRecoveryDialogService recoveryDialogs;
    private readonly IPortableSnapshotSettingsService settings;
    private readonly IPortableStorageInspector storageInspector;
    private readonly ISnapshotReader snapshotReader;
    private PortableSnapshotExportResult? lastExport;

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InspectDestinationCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveDefaultDirectoryCommand))]
    [NotifyCanExecuteChangedFor(nameof(TestSnapshotOpenCommand))]
    [NotifyCanExecuteChangedFor(nameof(DownloadInstallerCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InspectDestinationCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    private string _sourceSnapshotPath = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InspectDestinationCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveDefaultDirectoryCommand))]
    private string _destinationDirectory = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private string _defaultSnapshotDirectory = "지정되지 않음";

    [ObservableProperty]
    private string _storageSummary = "저장 위치를 선택하면 쓰기 권한, 여유 공간, 파일 시스템을 검사합니다.";

    [ObservableProperty]
    private string _statusText = "Recovery Snapshot을 외부 또는 동기화 폴더에 안전하게 내보낼 수 있습니다.";

    [ObservableProperty]
    private int _progressPercentage;

    [ObservableProperty]
    private bool _hasIndependentCopy;

    [ObservableProperty]
    private bool _isEncrypted;

    [ObservableProperty]
    private bool _isOtherSynchronizedFolder;

    [ObservableProperty]
    private bool _isExternalStorageConfirmed;

    [ObservableProperty]
    private bool _snapshotOpenTested;

    [ObservableProperty]
    private bool _installerStoredSeparately;

    [ObservableProperty]
    private string _requiredAccountsText = "Microsoft, Steam, Epic, Xbox, Adobe, Ableton, VPN, 브라우저, SSH Key";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadInstallerCommand))]
    private bool _downloadSpecificVersion;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadInstallerCommand))]
    private string _specificVersionTag = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadInstallerCommand))]
    private string _installerDestinationDirectory = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<PreResetChecklistRowViewModel> _checklistItems = [];

    public PortableSnapshotViewModel(
        IPortableStorageInspector storageInspector,
        IPortableSnapshotExporter exporter,
        IPortableSnapshotSettingsService settings,
        IPreResetChecklistService checklistService,
        IReplicaInstallerDownloadService installerDownloads,
        ISnapshotReader snapshotReader,
        IPortableSnapshotDialogService dialogs,
        IRecoveryDialogService recoveryDialogs)
    {
        this.storageInspector = storageInspector;
        this.exporter = exporter;
        this.settings = settings;
        this.checklistService = checklistService;
        this.installerDownloads = installerDownloads;
        this.snapshotReader = snapshotReader;
        this.dialogs = dialogs;
        this.recoveryDialogs = recoveryDialogs;
    }

    public async Task ShowAsync(CancellationToken cancellationToken = default)
    {
        IsVisible = true;
        try
        {
            PortableSnapshotSettings current = await settings.GetAsync(cancellationToken);
            DefaultSnapshotDirectory = current.DefaultSnapshotDirectory ?? "지정되지 않음";
            if (string.IsNullOrWhiteSpace(DestinationDirectory) &&
                current.DefaultSnapshotDirectory is not null)
            {
                DestinationDirectory = current.DefaultSnapshotDirectory;
                InstallerDestinationDirectory = Path.Combine(current.DefaultSnapshotDirectory, "Recovery");
            }
        }
        catch (PortableSnapshotException)
        {
            StatusText = "기본 Snapshot 폴더 설정을 안전하게 읽을 수 없습니다.";
        }
    }

    [RelayCommand]
    private void SelectSourceSnapshot()
    {
        string? path = dialogs.SelectSnapshot();
        if (path is null)
        {
            return;
        }

        SourceSnapshotPath = path;
        FileName = Path.GetFileName(path);
        SnapshotOpenTested = false;
        lastExport = null;
        ChecklistItems = [];
    }

    [RelayCommand]
    private void SelectDestination()
    {
        string? path = dialogs.SelectFolder("Snapshot 저장 위치 선택", DestinationDirectory);
        if (path is null)
        {
            return;
        }

        DestinationDirectory = path;
        InstallerDestinationDirectory = Path.Combine(path, "Recovery");
        lastExport = null;
        ChecklistItems = [];
    }

    [RelayCommand]
    private void SelectInstallerDestination()
    {
        string? path = dialogs.SelectFolder(
            "ReplicaSetup.exe를 별도로 저장할 Recovery 폴더 선택",
            InstallerDestinationDirectory);
        if (path is not null)
        {
            InstallerDestinationDirectory = path;
        }
    }

    [RelayCommand(CanExecute = nameof(CanInspect))]
    private async Task InspectDestinationAsync(CancellationToken cancellationToken)
    {
        await RunAsync(async () =>
        {
            long size = new FileInfo(SourceSnapshotPath).Length;
            PortableStorageInspection inspection = await storageInspector.InspectAsync(
                DestinationDirectory,
                size,
                cancellationToken);
            StorageSummary = FormatInspection(inspection) +
                (IsOtherSynchronizedFolder && inspection.CloudProvider is null
                    ? " · 사용자 지정 동기화 폴더: 동기화 완료와 충돌 복사본을 별도로 확인하세요."
                    : string.Empty);
            StatusText = inspection.CanExport
                ? "선택한 위치에 Snapshot을 저장할 수 있습니다."
                : "차단 항목을 해결한 뒤 다시 검사하세요.";
        });
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync(CancellationToken cancellationToken)
    {
        await RunAsync(async () =>
        {
            Progress<PortableExportProgress> progress = new(update =>
            {
                ProgressPercentage = update.TotalBytes <= 0
                    ? 0
                    : (int)Math.Clamp(update.ProcessedBytes * 100 / update.TotalBytes, 0, 100);
                StatusText = update.Stage switch
                {
                    PortableExportStage.Inspecting => "저장 위치 검사 중…",
                    PortableExportStage.HashingSource => "원본 Snapshot SHA-256 계산 중…",
                    PortableExportStage.Copying => "임시 파일로 복사 중…",
                    PortableExportStage.Verifying => "복사본 SHA-256 검증 중…",
                    _ => "Snapshot 내보내기 완료",
                };
            });
            lastExport = await exporter.ExportAsync(
                new PortableSnapshotExportRequest(
                    SourceSnapshotPath,
                    DestinationDirectory,
                    FileName,
                    IsOtherSynchronizedFolder,
                    IsExternalStorageConfirmed),
                progress,
                cancellationToken);
            TestSnapshotOpenCommand.NotifyCanExecuteChanged();
            ProgressPercentage = 100;
            StorageSummary = FormatInspection(lastExport.Storage);
            InstallerDestinationDirectory = Path.Combine(lastExport.Storage.DirectoryPath, "Recovery");
            UpdateChecklist();
            StatusText = $"Snapshot 저장 및 SHA-256 검증 완료: {lastExport.Sha256}";
        });
    }

    [RelayCommand(CanExecute = nameof(CanSaveDefaultDirectory))]
    private async Task SaveDefaultDirectoryAsync(CancellationToken cancellationToken)
    {
        await RunAsync(async () =>
        {
            await settings.SaveDefaultDirectoryAsync(DestinationDirectory, cancellationToken);
            DefaultSnapshotDirectory = Path.GetFullPath(DestinationDirectory);
            StatusText = "기본 Snapshot 폴더를 저장했습니다.";
        });
    }

    [RelayCommand(CanExecute = nameof(CanTestSnapshotOpen))]
    private async Task TestSnapshotOpenAsync(CancellationToken cancellationToken)
    {
        if (lastExport is null)
        {
            return;
        }

        await RunAsync(async () =>
        {
            char[]? password = null;
            try
            {
                try
                {
                    _ = await snapshotReader.ReadAsync(
                        new ReplicaSnapshotReadRequest(lastExport.DestinationPath),
                        null,
                        cancellationToken);
                }
                catch (ReplicaSnapshotDecryptionException)
                {
                    password = recoveryDialogs.RequestPassword(
                        "암호화된 Snapshot 열기 테스트",
                        "내보낸 Snapshot의 비밀번호를 입력하세요. 비밀번호는 저장되거나 기록되지 않습니다.");
                    if (password is null)
                    {
                        return;
                    }

                    _ = await snapshotReader.ReadAsync(
                        new ReplicaSnapshotReadRequest(lastExport.DestinationPath, password),
                        null,
                        cancellationToken);
                    IsEncrypted = true;
                }

                SnapshotOpenTested = true;
                UpdateChecklist();
                StatusText = "내보낸 Snapshot의 구조와 체크섬을 열기 테스트로 확인했습니다.";
            }
            finally
            {
                if (password is not null)
                {
                    Array.Clear(password);
                }
            }
        });
    }

    [RelayCommand(CanExecute = nameof(CanDownloadInstaller))]
    private async Task DownloadInstallerAsync(CancellationToken cancellationToken)
    {
        string versionDescription = DownloadSpecificVersion
            ? SpecificVersionTag
            : "최신 Stable";
        if (!dialogs.ConfirmInstallerDownload(InstallerDestinationDirectory, versionDescription))
        {
            return;
        }

        await RunAsync(async () =>
        {
            Progress<ReplicaInstallerDownloadProgress> progress = new(update =>
            {
                ProgressPercentage = update.TotalBytes <= 0
                    ? 0
                    : (int)Math.Clamp(update.ProcessedBytes * 100 / update.TotalBytes, 0, 100);
                StatusText = update.Stage switch
                {
                    ReplicaInstallerDownloadStage.ResolvingRelease => "공식 GitHub Release 확인 중…",
                    ReplicaInstallerDownloadStage.Downloading => "ReplicaSetup.exe 다운로드 중…",
                    ReplicaInstallerDownloadStage.Verifying => "ReplicaSetup.exe SHA-256 검증 중…",
                    _ => "ReplicaSetup.exe 보관 완료",
                };
            });
            ReplicaInstallerDownloadResult result = await installerDownloads.DownloadAsync(
                new ReplicaInstallerDownloadRequest(
                    InstallerDestinationDirectory,
                    DownloadSpecificVersion
                        ? ReplicaReleaseSelection.SpecificVersion
                        : ReplicaReleaseSelection.LatestStable,
                    DownloadSpecificVersion ? SpecificVersionTag : null,
                    UserApproved: true),
                progress,
                cancellationToken);
            InstallerStoredSeparately = true;
            UpdateChecklist();
            StatusText = $"{result.VersionTag} ReplicaSetup.exe 저장 완료. 자동 실행하지 않았습니다.";
        });
    }

    [RelayCommand]
    private void RefreshChecklist() => UpdateChecklist();

    partial void OnHasIndependentCopyChanged(bool value) => UpdateChecklist();

    partial void OnIsEncryptedChanged(bool value) => UpdateChecklist();

    partial void OnSnapshotOpenTestedChanged(bool value) => UpdateChecklist();

    partial void OnInstallerStoredSeparatelyChanged(bool value) => UpdateChecklist();

    partial void OnRequiredAccountsTextChanged(string value) => UpdateChecklist();

    private void UpdateChecklist()
    {
        if (lastExport is null)
        {
            return;
        }

        PreResetChecklist checklist = checklistService.Create(new PreResetChecklistRequest(
            lastExport,
            HasIndependentCopy,
            InstallerStoredSeparately,
            IsEncrypted,
            SnapshotOpenTested,
            RequiredAccountsText.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
        ChecklistItems = checklist.Items.Select(item => new PreResetChecklistRowViewModel(
            item.Status switch
            {
                PreResetChecklistStatus.Complete => "완료",
                PreResetChecklistStatus.Warning => "확인",
                _ => "필수",
            },
            item.Title,
            item.Guidance)).ToArray();
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
            StatusText = "작업을 취소했습니다. 불완전한 파일은 최종 이름으로 남지 않습니다.";
        }
        catch (Exception exception) when (
            exception is PortableSnapshotException or ReplicaInstallerDownloadException or
            ReplicaSnapshotException or IOException or UnauthorizedAccessException or HttpRequestException)
        {
            StatusText = "작업을 안전하게 완료하지 못했습니다. 대상 장치의 연결, 여유 공간과 쓰기 권한을 확인하세요.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanInspect() => !IsBusy && File.Exists(SourceSnapshotPath) && Directory.Exists(DestinationDirectory);

    private bool CanExport() => CanInspect() && !string.IsNullOrWhiteSpace(FileName);

    private bool CanSaveDefaultDirectory() => !IsBusy && Directory.Exists(DestinationDirectory);

    private bool CanTestSnapshotOpen() => !IsBusy && lastExport is not null && File.Exists(lastExport.DestinationPath);

    private bool CanDownloadInstaller() => !IsBusy &&
        Directory.Exists(InstallerDestinationDirectory) &&
        (!DownloadSpecificVersion || !string.IsNullOrWhiteSpace(SpecificVersionTag));

    private static string FormatInspection(PortableStorageInspection inspection)
    {
        string provider = inspection.CloudProvider is null ? string.Empty : $" · {inspection.CloudProvider}";
        string external = inspection.IsExternalStorage ? " · 외부 저장장치" : string.Empty;
        string issues = inspection.Issues.Count == 0
            ? "검사 항목 이상 없음"
            : string.Join(" · ", inspection.Issues.Select(issue => issue.Message));
        return $"{inspection.StorageKind}{provider}{external} · {inspection.FileSystem} · " +
            $"여유 공간 {inspection.AvailableFreeSpace:N0} B · {issues}";
    }
}

public sealed record PreResetChecklistRowViewModel(string Status, string Title, string Guidance);
