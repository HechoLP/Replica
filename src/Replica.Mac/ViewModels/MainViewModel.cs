using System.Reflection;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Replica.Core.Platforms;
using Replica.Core.Services;
using Replica.Core.Snapshots;
using Replica.Core.Updates;
using Replica.Mac.Infrastructure.Snapshots;
using Replica.Mac.Infrastructure.Updates;
using Replica.Mac.Services;

namespace Replica.Mac.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly IPlatformEnvironmentScanner scanner;
    private readonly IMacLightweightSnapshotService snapshotService;
    private readonly ISnapshotReader snapshotReader;
    private readonly IMacUpdateService updateService;
    private readonly IMacFileDialogService fileDialogs;
    private readonly string currentVersion;
    private CancellationTokenSource? activeOperation;
    private PlatformScanResult? lastScan;
    private Uri? releasePage;
    private bool isBusy;
    private bool isIndeterminate;
    private double progressValue;
    private string status = "현재 Mac을 스캔해 시작하세요.";
    private string snapshotDetails = "";
    private int applicationCount;
    private int homebrewCount;
    private int sensitiveCount;
    private int warningCount;
    private string warningSummary = string.Empty;

    public MainViewModel(
        IPlatformEnvironmentScanner scanner,
        IMacLightweightSnapshotService snapshotService,
        ISnapshotReader snapshotReader,
        IMacUpdateService updateService,
        IMacFileDialogService fileDialogs)
    {
        this.scanner = scanner;
        this.snapshotService = snapshotService;
        this.snapshotReader = snapshotReader;
        this.updateService = updateService;
        this.fileDialogs = fileDialogs;
        currentVersion = GetCurrentVersion();
        ScanCommand = new AsyncRelayCommand(ScanAsync, CanStartOperation);
        CreateSnapshotCommand = new AsyncRelayCommand(CreateSnapshotAsync, CanCreateSnapshot);
        OpenSnapshotCommand = new AsyncRelayCommand(OpenSnapshotAsync, CanStartOperation);
        CheckUpdateCommand = new AsyncRelayCommand(CheckUpdateAsync, CanStartOperation);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        OpenReleasePageCommand = new AsyncRelayCommand(OpenReleasePageAsync, () => HasReleasePage && !IsBusy);
    }

    public IAsyncRelayCommand ScanCommand { get; }

    public IAsyncRelayCommand CreateSnapshotCommand { get; }

    public IAsyncRelayCommand OpenSnapshotCommand { get; }

    public IAsyncRelayCommand CheckUpdateCommand { get; }

    public IRelayCommand CancelCommand { get; }

    public IAsyncRelayCommand OpenReleasePageCommand { get; }

    public string VersionLabel => $"버전 {currentVersion}";

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetProperty(ref isBusy, value))
            {
                NotifyCommandStates();
            }
        }
    }

    public bool IsIndeterminate
    {
        get => isIndeterminate;
        private set => SetProperty(ref isIndeterminate, value);
    }

    public double ProgressValue
    {
        get => progressValue;
        private set => SetProperty(ref progressValue, value);
    }

    public string Status
    {
        get => status;
        private set => SetProperty(ref status, value);
    }

    public string SnapshotDetails
    {
        get => snapshotDetails;
        private set => SetProperty(ref snapshotDetails, value);
    }

    public int ApplicationCount
    {
        get => applicationCount;
        private set => SetProperty(ref applicationCount, value);
    }

    public int HomebrewCount
    {
        get => homebrewCount;
        private set => SetProperty(ref homebrewCount, value);
    }

    public int SensitiveCount
    {
        get => sensitiveCount;
        private set => SetProperty(ref sensitiveCount, value);
    }

    public int WarningCount
    {
        get => warningCount;
        private set => SetProperty(ref warningCount, value);
    }

    public string WarningSummary
    {
        get => warningSummary;
        private set => SetProperty(ref warningSummary, value);
    }

    public bool HasReleasePage => releasePage is not null;

    private bool CanStartOperation() => !IsBusy;

    private bool CanCreateSnapshot() => !IsBusy && lastScan is not null;

    private async Task ScanAsync()
    {
        await RunOperationAsync(async cancellationToken =>
        {
            Status = "macOS 환경을 읽는 중입니다.";
            IsIndeterminate = false;
            Progress<PlatformScanProgress> progress = new(update =>
            {
                ProgressValue = update.TotalStages == 0 ? 0 : update.CompletedStages * 100d / update.TotalStages;
                Status = update.Status;
            });
            PlatformScanResult result = await scanner.ScanAsync(progress, cancellationToken);
            lastScan = result;
            ApplicationCount = result.Summary.ApplicationCount;
            HomebrewCount = result.Summary.HomebrewPackageCount;
            SensitiveCount = result.Summary.SensitiveExclusionCount;
            WarningCount = result.Summary.WarningCount;
            WarningSummary = string.Join(
                Environment.NewLine,
                result.Warnings.Select(warning => $"• {warning.Provider}: {warning.Message}"));
            SnapshotDetails = $"{result.Platform.DisplayName} {result.Platform.Version} · {result.Platform.Architecture} · 글꼴 {result.Summary.FontCount}개";
            Status = result.Warnings.Count == 0
                ? "스캔이 완료되었습니다. Lightweight Snapshot을 만들 수 있습니다."
                : "스캔이 완료됐지만 일부 항목을 읽지 못했습니다. 경고를 확인한 뒤 Snapshot을 만드세요.";
            ProgressValue = 100;
        });
    }

    private async Task CreateSnapshotAsync()
    {
        if (lastScan is null)
        {
            return;
        }

        string suggestedName = $"Replica-{DateTime.Now:yyyyMMdd-HHmmss}.replica";
        string? path = await fileDialogs.PickSnapshotDestinationAsync(suggestedName, CancellationToken.None);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        await RunOperationAsync(async cancellationToken =>
        {
            Status = "Snapshot을 안전하게 만드는 중입니다.";
            IsIndeterminate = false;
            Progress<ReplicaSnapshotProgress> progress = new(update =>
            {
                bool hasByteProgress = update.TotalBytes > 0;
                bool hasItemProgress = update.TotalItems > 0;
                IsIndeterminate = update.Stage != ReplicaSnapshotStage.Completed &&
                    !hasByteProgress && !hasItemProgress;
                ProgressValue = update.Stage == ReplicaSnapshotStage.Completed
                    ? 100
                    : hasByteProgress
                        ? (int)Math.Clamp(update.ProcessedBytes * 100 / update.TotalBytes, 0, 100)
                        : hasItemProgress
                            ? (int)Math.Clamp(update.CompletedItems * 100 / update.TotalItems, 0, 100)
                            : 0;
                Status = update.Stage switch
                {
                    ReplicaSnapshotStage.Estimating => "포함 항목을 확인하는 중입니다.",
                    ReplicaSnapshotStage.Staging => "Snapshot 데이터를 준비하는 중입니다.",
                    ReplicaSnapshotStage.Hashing => "SHA-256을 계산하는 중입니다.",
                    ReplicaSnapshotStage.Archiving => ".replica 컨테이너를 만드는 중입니다.",
                    ReplicaSnapshotStage.Validating => "완성된 Snapshot을 다시 검증하는 중입니다.",
                    _ => "Snapshot 생성이 완료되었습니다.",
                };
            });
            ReplicaSnapshotManifest manifest = await snapshotService.CreateAsync(
                path,
                currentVersion,
                lastScan,
                progress,
                cancellationToken);
            SnapshotDetails = $"저장 완료 · {Path.GetFileName(path)} · ID {manifest.SnapshotId:N}";
            Status = "Lightweight Snapshot을 생성하고 무결성을 확인했습니다.";
            ProgressValue = 100;
        });
    }

    private async Task OpenSnapshotAsync()
    {
        string? path = await fileDialogs.PickSnapshotToOpenAsync(CancellationToken.None);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        await OpenSnapshotPathAsync(path);
    }

    public Task OpenSnapshotPathAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return RunOperationAsync(async cancellationToken =>
        {
            Status = "Snapshot 구조와 SHA-256을 검증하는 중입니다.";
            IsIndeterminate = true;
            ReplicaSnapshotReadResult result = await snapshotReader.ReadAsync(
                new ReplicaSnapshotReadRequest(path),
                progress: null,
                cancellationToken);
            SnapshotDetails = $"{Path.GetFileName(path)} · {result.Manifest.SourcePlatform} · {result.Manifest.SnapshotType} · {result.Manifest.CreatedAtUtc.LocalDateTime:g}";
            Status = "Snapshot 무결성 검증을 통과했습니다. macOS에서는 복원 작업을 실행하지 않습니다.";
        });
    }

    private async Task CheckUpdateAsync()
    {
        await RunOperationAsync(async cancellationToken =>
        {
            Status = "GitHub Releases에서 업데이트를 확인하는 중입니다.";
            IsIndeterminate = true;
            MacUpdateInfo update = await updateService.CheckAsync(
                currentVersion,
                UpdatePreference.Default.Channel,
                cancellationToken);
            releasePage = update.ReleasePage;
            OnPropertyChanged(nameof(HasReleasePage));
            OpenReleasePageCommand.NotifyCanExecuteChanged();
            SnapshotDetails = update.LatestVersion is null ? string.Empty : $"현재 {update.CurrentVersion} · 최신 {update.LatestVersion}";
            Status = update.Message;
        });
    }

    private Task OpenReleasePageAsync()
    {
        return releasePage is null ? Task.CompletedTask : fileDialogs.OpenUriAsync(releasePage);
    }

    private void Cancel()
    {
        activeOperation?.Cancel();
    }

    private async Task RunOperationAsync(Func<CancellationToken, Task> operation)
    {
        if (IsBusy)
        {
            return;
        }

        activeOperation = new CancellationTokenSource();
        IsBusy = true;
        try
        {
            await operation(activeOperation.Token);
        }
        catch (OperationCanceledException) when (activeOperation.IsCancellationRequested)
        {
            Status = "작업을 취소했습니다.";
        }
        catch (ReplicaSnapshotDecryptionException)
        {
            Status = "Snapshot을 열 수 없습니다. 암호화된 Snapshot 비밀번호 입력은 macOS Preview에서 아직 지원하지 않습니다.";
        }
        catch (OperationCanceledException)
        {
            Status = "작업 시간이 초과됐습니다. 잠시 후 다시 시도하세요.";
        }
        catch (Exception exception) when (
            exception is ReplicaSnapshotException or
                IOException or
                UnauthorizedAccessException or
                HttpRequestException or
                JsonException)
        {
            Status = "작업을 완료하지 못했습니다. 파일, 네트워크 또는 Snapshot 상태를 확인하세요.";
        }
        catch (Exception)
        {
            Status = "예기치 않은 문제가 발생해 작업을 안전하게 중단했습니다.";
        }
        finally
        {
            activeOperation.Dispose();
            activeOperation = null;
            IsIndeterminate = false;
            IsBusy = false;
        }
    }

    private void NotifyCommandStates()
    {
        ScanCommand.NotifyCanExecuteChanged();
        CreateSnapshotCommand.NotifyCanExecuteChanged();
        OpenSnapshotCommand.NotifyCanExecuteChanged();
        CheckUpdateCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        OpenReleasePageCommand.NotifyCanExecuteChanged();
    }

    private static string GetCurrentVersion()
    {
        string? informationalVersion = typeof(MainViewModel).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        return informationalVersion?.Split('+', 2)[0] ?? "0.0.0";
    }
}
