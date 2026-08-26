using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Replica.App.Services;
using Replica.Core.History;
using Replica.Core.Services;
using Replica.Core.Snapshots;

namespace Replica.App.ViewModels;

public sealed partial class SnapshotHistoryViewModel : ObservableObject
{
    private readonly ISnapshotHistoryDialogService _deleteDialogs;
    private readonly IRecoveryDialogService _recoveryDialogs;
    private readonly RestoreDryRunViewModel _restoreDryRun;
    private readonly ISnapshotHistoryService _service;

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddSnapshotCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveDetailsCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSnapshotCommand))]
    [NotifyCanExecuteChangedFor(nameof(CompareCommand))]
    [NotifyCanExecuteChangedFor(nameof(CreatePastRestorePlanCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveDetailsCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSnapshotCommand))]
    private SnapshotHistoryRowViewModel? _selectedSnapshot;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompareCommand))]
    [NotifyCanExecuteChangedFor(nameof(CreatePastRestorePlanCommand))]
    private SnapshotHistoryRowViewModel? _fromSnapshot;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompareCommand))]
    private SnapshotHistoryRowViewModel? _toSnapshot;

    [ObservableProperty]
    private IReadOnlyList<SnapshotHistoryRowViewModel> _snapshots = [];

    [ObservableProperty]
    private IReadOnlyList<SnapshotComparisonRowViewModel> _comparisonItems = [];

    [ObservableProperty]
    private string _comparisonSummary = "비교할 두 Snapshot을 선택하세요.";

    [ObservableProperty]
    private string _statusText = "Snapshot 기록을 불러오면 로컬 메타데이터와 파일 상태를 확인할 수 있습니다.";

    public SnapshotHistoryViewModel(
        ISnapshotHistoryService service,
        IRecoveryDialogService recoveryDialogs,
        ISnapshotHistoryDialogService deleteDialogs,
        RestoreDryRunViewModel restoreDryRun)
    {
        _service = service;
        _recoveryDialogs = recoveryDialogs;
        _deleteDialogs = deleteDialogs;
        _restoreDryRun = restoreDryRun;
    }

    public async Task ShowAsync(CancellationToken cancellationToken = default)
    {
        IsVisible = true;
        await RefreshCoreAsync(cancellationToken);
    }

    public async Task OpenFromCommandLineAsync(
        string snapshotPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotPath);
        IsVisible = true;

        await RunAsync(async () =>
        {
            try
            {
                await _service.AddSnapshotAsync(
                    snapshotPath,
                    ReadOnlyMemory<char>.Empty,
                    null,
                    cancellationToken);
            }
            catch (ReplicaSnapshotDecryptionException)
            {
                char[]? password = _recoveryDialogs.RequestPassword(
                    "암호화 Snapshot",
                    "Snapshot을 열려면 비밀번호를 입력하세요. 비밀번호는 저장되지 않습니다.");
                if (password is null)
                {
                    return;
                }

                try
                {
                    await _service.AddSnapshotAsync(snapshotPath, password, null, cancellationToken);
                }
                finally
                {
                    Array.Clear(password);
                }
            }

            await LoadRowsAsync(cancellationToken);
            StatusText = "Snapshot을 검증하고 기록에 추가했습니다.";
        });
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task RefreshAsync(CancellationToken cancellationToken) => RefreshCoreAsync(cancellationToken);

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task AddSnapshotAsync(CancellationToken cancellationToken)
    {
        string? path = _recoveryDialogs.SelectRecoverySnapshot();
        if (path is null)
        {
            return;
        }

        await RunAsync(async () =>
        {
            try
            {
                await _service.AddSnapshotAsync(path, ReadOnlyMemory<char>.Empty, null, cancellationToken);
            }
            catch (ReplicaSnapshotDecryptionException)
            {
                char[]? password = _recoveryDialogs.RequestPassword(
                    "암호화 Snapshot",
                    "Snapshot 기록을 추가하려면 비밀번호를 입력하세요. 비밀번호는 DB에 저장되지 않습니다.");
                if (password is null)
                {
                    return;
                }

                try
                {
                    await _service.AddSnapshotAsync(path, password, null, cancellationToken);
                }
                finally
                {
                    Array.Clear(password);
                }
            }

            await LoadRowsAsync(cancellationToken);
            StatusText = "Snapshot 메타데이터와 비교 인덱스를 기록했습니다. Snapshot 본문은 DB에 저장하지 않았습니다.";
        });
    }

    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private async Task SaveDetailsAsync(CancellationToken cancellationToken)
    {
        if (SelectedSnapshot is null)
        {
            return;
        }

        SnapshotHistoryRowViewModel selected = SelectedSnapshot;
        await RunAsync(async () =>
        {
            await _service.UpdateSnapshotAsync(
                selected.SnapshotId,
                new SnapshotHistoryUpdate(
                    selected.Name,
                    selected.Description,
                    ParseTags(selected.TagsText)),
                cancellationToken);
            await LoadRowsAsync(cancellationToken, selected.SnapshotId);
            StatusText = "Snapshot 이름, 설명, 태그를 저장했습니다.";
        });
    }

    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private async Task DeleteSnapshotAsync(CancellationToken cancellationToken)
    {
        if (SelectedSnapshot is null)
        {
            return;
        }

        SnapshotHistoryRowViewModel selected = SelectedSnapshot;
        SnapshotDeleteChoice choice = _deleteDialogs.ConfirmDelete(selected.Name, selected.FileExists);
        if (choice == SnapshotDeleteChoice.Cancel)
        {
            return;
        }

        await RunAsync(async () =>
        {
            await _service.DeleteSnapshotAsync(
                selected.SnapshotId,
                choice == SnapshotDeleteChoice.DeleteSnapshotFile,
                userConfirmed: true,
                cancellationToken);
            await LoadRowsAsync(cancellationToken);
            StatusText = choice == SnapshotDeleteChoice.DeleteSnapshotFile
                ? "Snapshot 기록과 선택한 .replica 파일을 삭제했습니다."
                : "Snapshot 기록만 삭제했습니다. .replica 파일은 유지했습니다.";
        });
    }

    [RelayCommand(CanExecute = nameof(CanCompare))]
    private async Task CompareAsync(CancellationToken cancellationToken)
    {
        if (FromSnapshot is null || ToSnapshot is null)
        {
            return;
        }

        await RunAsync(async () =>
        {
            SnapshotComparisonResult result = await _service.CompareAsync(
                FromSnapshot.SnapshotId,
                ToSnapshot.SnapshotId,
                cancellationToken);
            ComparisonItems = result.Items.Select(item => new SnapshotComparisonRowViewModel(
                ChangeName(item.ChangeKind),
                AreaName(item.Area),
                item.DisplayName,
                item.BeforeValue ?? FormatBytes(item.BeforeSize),
                item.AfterValue ?? FormatBytes(item.AfterSize),
                FormatSizeDelta(item.BeforeSize, item.AfterSize),
                HashChanged(item))).ToArray();
            ComparisonSummary = $"{result.From.Name} → {result.To.Name} · " +
                $"추가 {result.AddedCount:N0} · 변경 {result.ChangedCount:N0} · 제거 {result.RemovedCount:N0}";
            StatusText = "저장된 크기, 수정 시각, Hash와 구조화 메타데이터만 사용해 비교했습니다.";
        });
    }

    [RelayCommand(CanExecute = nameof(CanCreatePastPlan))]
    private async Task CreatePastRestorePlanAsync(CancellationToken cancellationToken)
    {
        if (FromSnapshot is null)
        {
            return;
        }

        char[]? password = null;
        if (FromSnapshot.IsEncrypted)
        {
            password = _recoveryDialogs.RequestPassword(
                "암호화 Snapshot",
                "현재 PC와 비교해 과거 상태 복원 계획을 만들려면 비밀번호를 입력하세요.");
            if (password is null)
            {
                return;
            }
        }

        try
        {
            await RunAsync(async () =>
            {
                PastStateRestorePlan result = await _service.CreatePastStateRestorePlanAsync(
                    FromSnapshot.SnapshotId,
                    [],
                    password ?? [],
                    cancellationToken);
                _restoreDryRun.Display(result.Plan);
                StatusText = "과거 Snapshot과 현재 PC를 비교해 검토 대기 Restore Plan을 만들었습니다. 자동 실행하지 않습니다.";
            });
        }
        finally
        {
            if (password is not null)
            {
                Array.Clear(password);
            }
        }
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        await RunAsync(async () =>
        {
            await LoadRowsAsync(cancellationToken);
            StatusText = Snapshots.Count == 0
                ? "기록된 Snapshot이 없습니다. 기존 .replica 파일을 추가할 수 있습니다."
                : $"Snapshot {Snapshots.Count:N0}개의 기록과 파일 상태를 불러왔습니다.";
        });
    }

    private async Task LoadRowsAsync(CancellationToken cancellationToken, Guid? selectId = null)
    {
        IReadOnlyList<SnapshotHistoryEntry> entries = await _service.GetSnapshotsAsync(cancellationToken);
        Snapshots = entries.Select(entry => new SnapshotHistoryRowViewModel(entry)).ToArray();
        SelectedSnapshot = selectId is null
            ? Snapshots.FirstOrDefault()
            : Snapshots.FirstOrDefault(row => row.SnapshotId == selectId) ?? Snapshots.FirstOrDefault();
        FromSnapshot = Snapshots.FirstOrDefault();
        ToSnapshot = Snapshots.Skip(1).FirstOrDefault();
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
            StatusText = "Snapshot 기록 작업을 취소했습니다.";
        }
        catch (ReplicaSnapshotDecryptionException)
        {
            StatusText = "Snapshot을 열 수 없습니다. 비밀번호 또는 파일 상태를 확인하세요.";
        }
        catch (Exception)
        {
            StatusText = "Snapshot 기록 작업을 완료하지 못했습니다. 원본 Snapshot 파일은 변경하거나 삭제하지 않았습니다.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRun() => !IsBusy;

    private bool CanEditSelected() => SelectedSnapshot is not null && !IsBusy;

    private bool CanCompare() => FromSnapshot is not null && ToSnapshot is not null &&
        FromSnapshot.SnapshotId != ToSnapshot.SnapshotId && !IsBusy;

    private bool CanCreatePastPlan() => FromSnapshot is { FileExists: true } && !IsBusy;

    private static string[] ParseTags(string value) => value.Split(
        ',',
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string ChangeName(SnapshotComparisonChangeKind kind) => kind switch
    {
        SnapshotComparisonChangeKind.Added => "추가",
        SnapshotComparisonChangeKind.Changed => "변경",
        _ => "제거",
    };

    private static string AreaName(SnapshotComparisonArea area) => area switch
    {
        SnapshotComparisonArea.Application => "프로그램",
        SnapshotComparisonArea.PluginSetting => "설정",
        _ => "사용자 파일",
    };

    private static string FormatBytes(long? bytes) => bytes is null ? "—" : $"{bytes.Value:N0} B";

    private static string FormatSizeDelta(long? before, long? after) => before is null || after is null
        ? "—"
        : $"{after.Value - before.Value:+#,0;-#,0;0} B";

    private static string HashChanged(SnapshotComparisonItem item) =>
        item.BeforeSha256 is not null && item.AfterSha256 is not null &&
        !item.BeforeSha256.Equals(item.AfterSha256, StringComparison.OrdinalIgnoreCase)
            ? "변경"
            : "—";
}

public sealed partial class SnapshotHistoryRowViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayLabel))]
    private string _name;

    [ObservableProperty]
    private string _description;

    [ObservableProperty]
    private string _tagsText;

    public SnapshotHistoryRowViewModel(SnapshotHistoryEntry entry)
    {
        SnapshotId = entry.SnapshotId;
        _name = entry.Name;
        _description = entry.Description;
        _tagsText = string.Join(", ", entry.Tags);
        FilePath = entry.FilePath;
        CreatedAt = entry.CreatedAtUtc.ToLocalTime().ToString("g");
        SnapshotType = entry.SnapshotType.ToString();
        SourceMachineName = entry.SourceMachineName;
        FileSize = $"{entry.FileSize:N0} B";
        IsEncrypted = entry.IsEncrypted;
        FileExists = entry.FileExists;
        EnvironmentScore = entry.EnvironmentScore is int score ? $"{score}%" : "—";
    }

    public Guid SnapshotId { get; }

    public string FilePath { get; }

    public string CreatedAt { get; }

    public string SnapshotType { get; }

    public string SourceMachineName { get; }

    public string FileSize { get; }

    public bool IsEncrypted { get; }

    public bool FileExists { get; }

    public string EnvironmentScore { get; }

    public string DisplayLabel => $"{Name} ({CreatedAt})";
}

public sealed record SnapshotComparisonRowViewModel(
    string Change,
    string Area,
    string Name,
    string Before,
    string After,
    string SizeDelta,
    string Hash);
