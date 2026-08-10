using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Replica.App.Services;
using Replica.Core.Scanning;
using Replica.Core.Services;
using Replica.Core.Snapshots;

namespace Replica.App.ViewModels;

public sealed partial class SnapshotBuilderViewModel : ObservableObject
{
    private readonly IAppVersionService appVersion;
    private readonly IPortableSnapshotDialogService dialogs;
    private readonly ISnapshotHistoryService history;
    private readonly IRecoveryDialogService recoveryDialogs;
    private readonly ReplicaUiSession session;
    private readonly ISnapshotSelectionEstimator estimator;
    private readonly ISnapshotWriter writer;

    [ObservableProperty]
    private SnapshotType _snapshotType = SnapshotType.Lightweight;

    [ObservableProperty]
    private IReadOnlyList<SnapshotCategoryOptionViewModel> _categories;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedFolderCommand))]
    private IReadOnlyList<SelectedFolderRowViewModel> _selectedFolders = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EstimateCommand))]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    private string _destinationPath = string.Empty;

    [ObservableProperty]
    private bool _isEncryptionEnabled;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    private bool _isFinalApprovalChecked;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EstimateCommand))]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    [NotifyCanExecuteChangedFor(nameof(SelectDestinationCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddSelectedFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedFolderCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private int _progressPercentage;

    [ObservableProperty]
    private string _estimatedSizeText = "예상 용량을 계산하지 않았습니다.";

    [ObservableProperty]
    private string _statusText = "Snapshot 종류와 포함 범위를 검토하세요. 아직 파일을 만들지 않았습니다.";

    [ObservableProperty]
    private SelectedFolderRowViewModel? _selectedFolder;

    [ObservableProperty]
    private IReadOnlyList<string> _sensitiveExclusions =
    [
        "TOKEN, SECRET, PASSWORD, KEY, CREDENTIAL, CONNECTION_STRING 값",
        "브라우저 프로필과 로그인 세션",
        "개인키, 라이선스, 결제 및 복구 키",
        "AppData·Windows·Program Files·ProgramData 전체",
        "Cache, 로그, 임시 파일과 대용량 설치 폴더",
    ];

    public SnapshotBuilderViewModel(
        ReplicaUiSession session,
        ISnapshotWriter writer,
        ISnapshotSelectionEstimator estimator,
        IAppVersionService appVersion,
        ISnapshotHistoryService history,
        IPortableSnapshotDialogService dialogs,
        IRecoveryDialogService recoveryDialogs)
    {
        this.session = session;
        this.writer = writer;
        this.estimator = estimator;
        this.appVersion = appVersion;
        this.history = history;
        this.dialogs = dialogs;
        this.recoveryDialogs = recoveryDialogs;
        _categories = CreateCategories();
        foreach (SnapshotCategoryOptionViewModel category in _categories)
        {
            category.PropertyChanged += (_, _) =>
            {
                IsFinalApprovalChecked = false;
                EstimatedSizeText = "포함 카테고리가 변경되었습니다. 범위를 다시 검토하세요.";
            };
        }
    }

    public bool HasCurrentScan => session.LatestScan is not null;

    public void Configure(SnapshotType snapshotType)
    {
        SnapshotType = snapshotType;
        SelectedFolders = [];
        SelectedFolder = null;
        IsEncryptionEnabled = false;
        IsFinalApprovalChecked = false;
        ProgressPercentage = 0;
        EstimatedSizeText = snapshotType == SnapshotType.Lightweight
            ? "Lightweight Snapshot은 선택 파일을 포함하지 않습니다."
            : "사용자가 선택한 폴더만 용량을 계산하고 포함합니다.";
        StatusText = $"{GetTypeName(snapshotType)} 구성을 검토 중입니다. 최종 승인 전에는 생성하지 않습니다.";
        OnPropertyChanged(nameof(HasCurrentScan));
        EstimateCommand.NotifyCanExecuteChanged();
        CreateCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSelectDestination))]
    private void SelectDestination()
    {
        string? currentDirectory = string.IsNullOrWhiteSpace(DestinationPath)
            ? null
            : Path.GetDirectoryName(DestinationPath);
        string? directory = dialogs.SelectFolder("Snapshot 저장 폴더 선택", currentDirectory);
        if (directory is null)
        {
            return;
        }

        DestinationPath = Path.Combine(
            directory,
            $"Replica-{SnapshotType}-{DateTime.Now:yyyyMMdd-HHmmss}.replica");
        StatusText = "저장 위치를 선택했습니다. 기존 파일은 자동으로 덮어쓰지 않습니다.";
    }

    [RelayCommand(CanExecute = nameof(CanAddFolder))]
    private void AddSelectedFolder(ReplicaSelectedFolderCategory category)
    {
        if (SnapshotType == SnapshotType.Lightweight)
        {
            StatusText = "Lightweight Snapshot에는 사용자 파일을 포함할 수 없습니다.";
            return;
        }

        string? folder = dialogs.SelectFolder($"{GetFolderCategoryName(category)} 폴더 선택", null);
        if (folder is null || SelectedFolders.Any(row =>
                row.SourcePath.Equals(folder, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        SelectedFolderRowViewModel row = new(
            folder,
            $"files/selected/{category.ToString().ToLowerInvariant()}/{Guid.NewGuid():N}",
            category,
            GetFolderCategoryName(category));
        SelectedFolders = [.. SelectedFolders, row];
        SelectedFolder = row;
        EstimatedSizeText = "선택 폴더가 변경되었습니다. 예상 용량을 다시 계산하세요.";
        IsFinalApprovalChecked = false;
        RemoveSelectedFolderCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRemoveFolder))]
    private void RemoveSelectedFolder()
    {
        if (SelectedFolder is null)
        {
            return;
        }

        SelectedFolders = SelectedFolders.Where(row => row != SelectedFolder).ToArray();
        SelectedFolder = SelectedFolders.FirstOrDefault();
        EstimatedSizeText = "선택 폴더가 변경되었습니다. 예상 용량을 다시 계산하세요.";
        IsFinalApprovalChecked = false;
    }

    [RelayCommand(CanExecute = nameof(CanEstimate), IncludeCancelCommand = true)]
    private async Task EstimateAsync(CancellationToken cancellationToken)
    {
        await RunAsync(async () =>
        {
            ReplicaSelectionEstimate estimate = await estimator.EstimateAsync(
                CreateSelectedFolders(),
                [],
                cancellationToken);
            EstimatedSizeText = $"포함 파일 {estimate.Files.Count:N0}개 · 예상 {FormatBytes(estimate.TotalSize)} · " +
                $"제외 {estimate.Exclusions.Count:N0}개";
            StatusText = "예상 용량 계산을 완료했습니다. 민감 제외 목록과 저장 위치를 다시 검토하세요.";
        }, "예상 용량 계산");
    }

    [RelayCommand(CanExecute = nameof(CanCreate), IncludeCancelCommand = true)]
    private async Task CreateAsync(CancellationToken cancellationToken)
    {
        EnvironmentScanResult? scan = session.LatestScan;
        if (scan is null)
        {
            StatusText = "Snapshot을 만들기 전에 현재 PC 읽기 전용 스캔을 완료하세요.";
            return;
        }

        char[]? password = null;
        if (IsEncryptionEnabled)
        {
            password = recoveryDialogs.RequestPassword(
                "Snapshot 암호화",
                "비밀번호는 저장되거나 기록되지 않습니다. 잊으면 Snapshot을 복구할 수 없습니다.");
            if (password is null || password.Length == 0)
            {
                password?.AsSpan().Clear();
                StatusText = "암호화 비밀번호가 입력되지 않아 Snapshot 생성을 시작하지 않았습니다.";
                return;
            }
        }

        try
        {
            await RunAsync(async () =>
            {
                Progress<ReplicaSnapshotProgress> progress = new(update =>
                {
                    ProgressPercentage = update.TotalBytes > 0
                        ? (int)Math.Clamp(update.ProcessedBytes * 100 / update.TotalBytes, 0, 100)
                        : update.TotalItems > 0
                            ? (int)Math.Clamp(update.CompletedItems * 100 / update.TotalItems, 0, 100)
                            : 0;
                    StatusText = $"Snapshot 생성 중 · {GetStageName(update.Stage)}";
                });
                ReplicaSnapshotWriteRequest request = CreateRequest(scan, password);
                await writer.WriteAsync(request, progress, cancellationToken);
                session.LatestSnapshotPath = request.DestinationPath;
                try
                {
                    await history.AddSnapshotAsync(
                        request.DestinationPath,
                        password ?? [],
                        null,
                        cancellationToken);
                }
                catch (Exception exception) when (
                    exception is not OperationCanceledException and not OutOfMemoryException)
                {
                    StatusText = "Snapshot은 생성했지만 기록 인덱스에 추가하지 못했습니다. 파일은 그대로 유지됩니다.";
                    return;
                }

                ProgressPercentage = 100;
                IsFinalApprovalChecked = false;
                StatusText = $"Snapshot 생성 완료: {request.DestinationPath}";
            }, "Snapshot 생성");
        }
        finally
        {
            password?.AsSpan().Clear();
        }
    }

    private ReplicaSnapshotWriteRequest CreateRequest(EnvironmentScanResult scan, char[]? password)
    {
        WindowsEnvironmentInfo windows = scan.Windows ?? new WindowsEnvironmentInfo(
            "Windows 11",
            Environment.OSVersion.Version.ToString(),
            Environment.OSVersion.Version.Build.ToString(CultureInfo.InvariantCulture),
            RuntimeInformation.OSArchitecture.ToString(),
            CultureInfo.CurrentCulture.Name,
            TimeZoneInfo.Local.Id,
            Environment.MachineName,
            false,
            false,
            false,
            false);
        ReplicaWindowsInfo snapshotWindows = new(
            windows.Edition,
            windows.Version,
            windows.Build,
            windows.Architecture,
            windows.Locale,
            windows.TimeZone,
            CreateCapabilities(windows));
        ReplicaSnapshotInventory inventory = new(
            IsCategorySelected("applications")
                ? scan.Applications.Select(MapApplication).ToArray()
                : [],
            new ReplicaEnvironmentInventory(
                IsCategorySelected("environment")
                    ? scan.EnvironmentVariables
                        .Where(variable => !variable.IsSensitive && variable.Value is not null)
                        .Select(variable => new ReplicaEnvironmentVariable(
                            variable.Name,
                            variable.Value!,
                            variable.Scope))
                        .ToArray()
                    : [],
                IsCategorySelected("environment")
                    ? scan.PathEntries.Select(entry => new ReplicaPathEntry(
                        entry.Value,
                        entry.Scope,
                        entry.Order)).ToArray()
                    : []),
            snapshotWindows,
            IsCategorySelected("fonts")
                ? scan.Fonts.Select(font => new ReplicaFontInfo(
                    font.FamilyName,
                    font.Style,
                    null,
                    null)).ToArray()
                : [],
            IsCategorySelected("plugins")
                ? scan.BuiltInPluginIds.Select(id => new ReplicaPluginSnapshot(
                    id,
                    appVersion.DisplayVersion,
                    [],
                    [])).ToArray()
                : []);
        IReadOnlyList<ReplicaExclusion> exclusions =
        [
            .. scan.EnvironmentVariables
                .Where(variable => variable.IsSensitive)
                .Select(variable => new ReplicaExclusion(
                    variable.Name,
                    "SensitiveEnvironmentVariable",
                    variable.ExclusionReason)),
            new("AppData", "BroadLocationExcluded"),
            new("Windows", "BroadLocationExcluded"),
            new("Program Files", "BroadLocationExcluded"),
            new("ProgramData", "BroadLocationExcluded"),
            new("BrowserProfiles", "SensitiveProfileExcluded"),
            new("CacheAndLogs", "TransientDataExcluded"),
        ];
        return new ReplicaSnapshotWriteRequest(
            DestinationPath,
            SnapshotType,
            appVersion.DisplayVersion,
            new ReplicaMachineInfo(
                windows.MachineName,
                snapshotWindows,
                windows.Architecture,
                windows.Locale),
            inventory,
            CreateCapabilities(windows),
            exclusions,
            new ReplicaRecoveryOptions(
                "RenameAndKeepBoth",
                0,
                null,
                CreateSelectedFolders(),
                []),
            password is null ? null : new ReplicaSnapshotEncryptionOptions(password));
    }

    private static ReplicaApplication MapApplication(ScannedApplication application) => new(
        application.Name,
        application.Version,
        application.Publisher,
        application.Architecture,
        application.Scope,
        application.InstallType,
        new ReplicaPackageIdentity(
            application.WinGetId,
            application.PackageFamilyName,
            application.MsiProductCode),
        []);

    private static IReadOnlyList<string> CreateCapabilities(WindowsEnvironmentInfo windows)
    {
        List<string> capabilities = ["Windows11", "SnapshotV1"];
        if (windows.IsWinGetAvailable)
        {
            capabilities.Add("WinGet");
        }

        if (windows.IsPowerShellAvailable)
        {
            capabilities.Add("PowerShell");
        }

        if (windows.IsWindowsTerminalAvailable)
        {
            capabilities.Add("WindowsTerminal");
        }

        return capabilities;
    }

    private IReadOnlyList<ReplicaSelectedFolder> CreateSelectedFolders() =>
        SnapshotType == SnapshotType.Lightweight
            ? []
            : SelectedFolders.Select(row => new ReplicaSelectedFolder(
                row.SourcePath,
                row.ArchivePath,
                row.Category)).ToArray();

    private bool IsCategorySelected(string id) => Categories.Any(category =>
        category.Id.Equals(id, StringComparison.Ordinal) && category.IsSelected);

    private async Task RunAsync(Func<Task> operation, string operationName)
    {
        IsBusy = true;
        try
        {
            await operation();
        }
        catch (OperationCanceledException)
        {
            StatusText = $"{operationName}을 취소했습니다. 불완전한 파일은 사용되지 않습니다.";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            StatusText = $"{operationName}을 완료하지 못했습니다. ({exception.GetType().Name})";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSelectDestination() => !IsBusy;

    private bool CanAddFolder() => !IsBusy;

    private bool CanRemoveFolder() => SelectedFolder is not null && !IsBusy;

    private bool CanEstimate() => !IsBusy;

    private bool CanCreate() => !IsBusy &&
        IsFinalApprovalChecked &&
        session.LatestScan is not null &&
        !string.IsNullOrWhiteSpace(DestinationPath) &&
        DestinationPath.EndsWith(".replica", StringComparison.OrdinalIgnoreCase);

    partial void OnSelectedFolderChanged(SelectedFolderRowViewModel? value) =>
        RemoveSelectedFolderCommand.NotifyCanExecuteChanged();

    partial void OnDestinationPathChanged(string value)
    {
        IsFinalApprovalChecked = false;
    }

    partial void OnIsEncryptionEnabledChanged(bool value)
    {
        IsFinalApprovalChecked = false;
    }

    private static IReadOnlyList<SnapshotCategoryOptionViewModel> CreateCategories() =>
    [
        new("applications", "프로그램", "설치 프로그램과 복원 가능한 Package ID", true),
        new("windows", "Windows 정보", "Edition, Build, Locale과 호환성 정보", true),
        new("environment", "환경변수 및 PATH", "민감 값은 자동 제외", true),
        new("fonts", "글꼴", "메타데이터만 기록하며 상용 파일은 포함하지 않음", true),
        new("plugins", "Built-in Plugin 설정", "공식 플러그인의 허용 목록 데이터", true),
    ];

    private static string GetTypeName(SnapshotType type) => type switch
    {
        SnapshotType.Lightweight => "Lightweight Snapshot",
        SnapshotType.Recovery => "Recovery Snapshot",
        _ => "Offline Recovery Pack",
    };

    private static string GetFolderCategoryName(ReplicaSelectedFolderCategory category) => category switch
    {
        ReplicaSelectedFolderCategory.Desktop => "Desktop",
        ReplicaSelectedFolderCategory.Documents => "Documents",
        ReplicaSelectedFolderCategory.Project => "프로젝트",
        ReplicaSelectedFolderCategory.GameSave => "게임 세이브",
        _ => "프로그램 설정",
    };

    private static string GetStageName(ReplicaSnapshotStage stage) => stage switch
    {
        ReplicaSnapshotStage.Estimating => "용량 계산",
        ReplicaSnapshotStage.Staging => "파일 준비",
        ReplicaSnapshotStage.Hashing => "SHA-256 계산",
        ReplicaSnapshotStage.Archiving => "Archive 생성",
        ReplicaSnapshotStage.Validating => "무결성 검증",
        _ => "완료",
    };

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(bytes, 0);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }
}

public sealed partial class SnapshotCategoryOptionViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    public SnapshotCategoryOptionViewModel(string id, string name, string description, bool isSelected)
    {
        Id = id;
        Name = name;
        Description = description;
        _isSelected = isSelected;
    }

    public string Id { get; }

    public string Name { get; }

    public string Description { get; }
}

public sealed record SelectedFolderRowViewModel(
    string SourcePath,
    string ArchivePath,
    ReplicaSelectedFolderCategory Category,
    string CategoryName);
