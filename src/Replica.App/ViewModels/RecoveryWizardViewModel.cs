using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Replica.App.Services;
using Replica.Core.Execution;
using Replica.Core.Planning;
using Replica.Core.Recovery;
using Replica.Core.Services;
using Replica.Core.Snapshots;

namespace Replica.App.ViewModels;

public sealed partial class RecoveryWizardViewModel : ObservableObject
{
    private readonly IRecoveryDialogService _dialogs;
    private readonly IRecoveryWizardService _wizard;
    private RecoveryWizardSession? _session;
    private CancellationTokenSource? _activeOperation;

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApprovePlanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCommand))]
    [NotifyCanExecuteChangedFor(nameof(RetryFailedCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApproveRestartCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmResumeCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportOfflineInstallersCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "Recovery Snapshot을 선택해 복구를 시작하세요.";

    [ObservableProperty]
    private string _sourceComputerText = string.Empty;

    [ObservableProperty]
    private string _scoreText = string.Empty;

    [ObservableProperty]
    private string _resultText = string.Empty;

    [ObservableProperty]
    private string _currentStepText = "준비 · Recovery Snapshot 선택";

    public RecoveryWizardViewModel(
        IRecoveryWizardService wizard,
        IRecoveryDialogService dialogs)
    {
        _wizard = wizard;
        _dialogs = dialogs;
        Steps = new ObservableCollection<RecoveryStepRow>(CreateSteps());
        Phases = new ObservableCollection<RecoveryPhaseRow>(CreatePhases());
    }

    public ObservableCollection<RecoveryStepRow> Steps { get; }

    public ObservableCollection<RecoveryPhaseRow> Phases { get; }

    public ObservableCollection<RecoveryPathMappingRow> PathMappings { get; } = [];

    public ObservableCollection<RecoveryHardwareDifference> HardwareDifferences { get; } = [];

    public ObservableCollection<RecoveryManualAction> ManualActions { get; } = [];

    public ObservableCollection<RestoreActionExecutionResult> ActionResults { get; } = [];

    public ObservableCollection<RestoreAction> PlanActions { get; } = [];

    public RestorePlan? Plan => _session?.Plan;

    public bool CanAnalyze => !IsBusy && _session is
    {
        Status: RecoveryWizardStatus.InProgress,
        CurrentStep: RecoveryWizardStep.ShowSourceComputer or RecoveryWizardStep.ConfirmFileDestinations,
    };

    public bool CanApprovePlan => !IsBusy && _session?.Status == RecoveryWizardStatus.AwaitingApproval;

    public bool CanExecute => !IsBusy && _session is
    { Status: RecoveryWizardStatus.InProgress, Plan.IsApproved: true, ReviewBindingSha256: not null };

    public bool CanRetry => !IsBusy && _session is { CanRetry: true, ReviewBindingSha256: not null };

    public bool CanApproveRestart => !IsBusy &&
        _session?.Status == RecoveryWizardStatus.AwaitingRestartApproval;

    public bool CanConfirmResume => !IsBusy &&
        _session?.Status == RecoveryWizardStatus.AwaitingResumeConfirmation;

    public bool HasOfflineInstallers => ManualActions.Any(action =>
        action.ReasonCode.Equals("OfflineInstallerAvailable", StringComparison.Ordinal));

    public string PlanSummary => Plan is null
        ? string.Empty
        : $"설치 {Plan.DryRun.InstallPackageCount} · 업데이트 {Plan.DryRun.UpdatePackageCount} · " +
          $"설정 {Plan.DryRun.RestoreSettingsCount} · 사용자 파일 {Plan.DryRun.RestoreSelectedUserFileCount} · " +
          $"관리자 {Plan.DryRun.AdministratorActionCount} · 수동 {Plan.DryRun.ManualActionCount}";

    public async Task BeginAsync(CancellationToken cancellationToken = default)
    {
        IsVisible = true;
        string? path = _dialogs.SelectRecoverySnapshot();
        if (path is null)
        {
            StatusText = "Snapshot 선택을 취소했습니다. PC는 변경되지 않았습니다.";
            return;
        }

        await RunAsync(async operationToken =>
        {
            RecoveryStartResult started = await _wizard.StartAsync(
                path,
                ReadOnlyMemory<char>.Empty,
                operationToken);
            if (started.PasswordRequired)
            {
                char[]? password = _dialogs.RequestPassword(
                    "암호화 Snapshot",
                    "Snapshot 비밀번호를 입력하세요. 비밀번호는 저장하거나 기록하지 않습니다.");
                if (password is null)
                {
                    StatusText = "비밀번호 입력을 취소했습니다.";
                    return;
                }

                try
                {
                    started = await _wizard.StartAsync(path, password, operationToken);
                }
                finally
                {
                    Array.Clear(password);
                }
            }

            if (started.Session is not null)
            {
                Apply(started.Session);
                StatusText = "Snapshot 무결성을 확인했습니다. 생성 PC 정보를 검토한 뒤 현재 PC를 분석하세요.";
            }
        }, cancellationToken);
    }

    public async Task LoadResumeAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await RunAsync(async operationToken =>
        {
            RecoveryWizardSession? session = await _wizard.LoadForResumeAsync(
                sessionId,
                operationToken);
            if (session is null)
            {
                return;
            }

            IsVisible = true;
            Apply(session);
            StatusText = "재부팅 전 복구 상태를 확인했습니다. 계속을 선택해야 최종 검증을 시작합니다.";
        }, cancellationToken);
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task AnalyzeAsync(CancellationToken cancellationToken)
    {
        if (_session is null)
        {
            return;
        }

        await WithPasswordAsync(async (password, operationToken) =>
        {
            RecoveryWizardSession analyzed = await _wizard.AnalyzeAsync(
                _session.SessionId,
                PathMappings.Select(row => row.ToModel()).ToArray(),
                password,
                operationToken);
            Apply(analyzed);
            StatusText = analyzed.FailureReasonCode == "InsufficientStorage"
                ? $"대상 드라이브 공간이 부족합니다. 필요 {FormatBytes(analyzed.RequiredBytes)} · " +
                  $"사용 가능 {FormatBytes(analyzed.AvailableBytes)}. 경로 매핑을 바꾼 뒤 다시 분석하세요."
                : "차이 분석과 복원 계획을 만들었습니다. 계획을 검토하고 최종 승인하세요.";
        }, cancellationToken);
    }

    [RelayCommand]
    private async Task ApprovePlanAsync(CancellationToken cancellationToken)
    {
        if (_session is null || !_dialogs.Confirm(
                "복원 계획 최종 승인",
                "선택된 설치, 설정 및 파일 복원 작업을 승인하시겠습니까? Extra 앱 삭제와 자동 다운그레이드는 수행하지 않습니다."))
        {
            return;
        }

        await RunAsync(async operationToken =>
        {
            string binding = _session.ReviewBindingSha256 ??
                throw new InvalidOperationException("The displayed recovery review is no longer available.");
            Apply(await _wizard.ApprovePlanAsync(_session.SessionId, binding, operationToken));
            StatusText = "복원 계획이 승인되었습니다. 실행 전 요약을 다시 확인하세요.";
        }, cancellationToken);
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private Task ExecuteAsync(CancellationToken cancellationToken) => ExecuteWithPasswordAsync(
        retry: false,
        cancellationToken);

    [RelayCommand(IncludeCancelCommand = true)]
    private Task RetryFailedAsync(CancellationToken cancellationToken) => ExecuteWithPasswordAsync(
        retry: true,
        cancellationToken);

    [RelayCommand]
    private async Task ApproveRestartAsync(CancellationToken cancellationToken)
    {
        if (_session is null || !_dialogs.Confirm(
                "재부팅 후 복구 계속",
                "Windows 로그인 후 Replica를 다시 열도록 등록하시겠습니까? 지금 자동으로 재부팅되지는 않습니다."))
        {
            return;
        }

        await RunAsync(async operationToken =>
        {
            Apply(await _wizard.ApproveRestartAsync(_session.SessionId, true, operationToken));
            StatusText = "로그인 후 재개가 등록되었습니다. 준비가 되면 Windows를 직접 재부팅하세요.";
        }, cancellationToken);
    }

    [RelayCommand]
    private async Task ConfirmResumeAsync(CancellationToken cancellationToken)
    {
        if (_session is null || !_dialogs.Confirm(
                "복구 계속",
                "완료된 작업은 다시 실행하지 않고 최종 검증을 계속하시겠습니까?"))
        {
            return;
        }

        await WithPasswordAsync(async (password, operationToken) =>
        {
            Apply(await _wizard.ConfirmResumeAsync(
                _session.SessionId,
                password,
                operationToken));
            StatusText = "재부팅 후 검증을 완료했습니다.";
        }, cancellationToken);
    }

    [RelayCommand]
    private async Task CancelAsync(CancellationToken cancellationToken)
    {
        if (IsBusy)
        {
            _activeOperation?.Cancel();
            StatusText = "현재 단계의 안전한 중단을 요청했습니다. 진행 중인 파일 변경은 먼저 기록됩니다.";
            return;
        }

        if (_session is null)
        {
            IsVisible = false;
            return;
        }

        await RunAsync(async operationToken =>
        {
            Apply(await _wizard.CancelAsync(_session.SessionId, operationToken));
            StatusText = "복구를 취소했습니다. 완료된 변경은 롤백 화면에서 검토할 수 있습니다.";
        }, cancellationToken);
    }

    [RelayCommand(CanExecute = nameof(CanExportOfflineInstallers), IncludeCancelCommand = true)]
    private async Task ExportOfflineInstallersAsync(CancellationToken cancellationToken)
    {
        if (_session is null)
        {
            return;
        }

        string? destination = _dialogs.SelectOfflineInstallerExportFolder();
        int count = ManualActions.Count(action =>
            action.ReasonCode.Equals("OfflineInstallerAvailable", StringComparison.Ordinal));
        if (destination is null || !_dialogs.Confirm(
                "오프라인 설치 파일 내보내기",
                $"검증된 설치 파일 {count:N0}개를 다음 폴더에 내보낼까요?\n\n{destination}\n\n" +
                "기존 파일은 덮어쓰지 않으며 Replica가 설치 파일을 자동 실행하지 않습니다."))
        {
            return;
        }

        await WithPasswordAsync(async (password, operationToken) =>
        {
            OfflineInstallerExportResult result = await _wizard.ExportOfflineInstallersAsync(
                _session.SessionId,
                destination,
                password,
                operationToken);
            StatusText = $"서명과 SHA-256을 다시 확인한 설치 파일 {result.Installers.Count:N0}개 " +
                $"({FormatBytes(result.TotalBytes)})를 내보냈습니다. 실행 전 공급자와 라이선스를 확인하세요.";
        }, cancellationToken);
    }

    private Task ExecuteWithPasswordAsync(bool retry, CancellationToken cancellationToken)
    {
        if (_session is null)
        {
            return Task.CompletedTask;
        }

        return WithPasswordAsync(async (password, operationToken) =>
        {
            string binding = _session.ReviewBindingSha256 ??
                throw new InvalidOperationException("The approved recovery review is no longer available.");
            RecoveryWizardSession result = retry
                ? await _wizard.RetryFailedAsync(_session.SessionId, binding, password, operationToken)
                : await _wizard.ExecuteAsync(_session.SessionId, binding, password, operationToken);
            Apply(result);
            StatusText = result.Status switch
            {
                _ when result.FailureReasonCode is "PayloadCleanupPending" or
                    "UserCancelledPayloadCleanupPending" =>
                    "복구 작업은 중단되었지만 임시 복호화 파일 일부를 아직 지우지 못했습니다. Replica를 다시 연 뒤 이 복구 세션을 취소하거나 다시 시도하세요.",
                RecoveryWizardStatus.AwaitingRestartApproval =>
                    "선택된 작업을 마쳤으며 재부팅이 필요합니다. 자동 재부팅은 하지 않습니다.",
                RecoveryWizardStatus.Completed => "복원과 최종 검증을 완료했습니다.",
                RecoveryWizardStatus.Cancelled => "실행을 취소했습니다.",
                _ when result.CanRetry => "일부 작업이 실패했습니다. 실패한 작업만 다시 시도할 수 있습니다.",
                _ => "복원 실행 결과를 확인하세요.",
            };
        }, cancellationToken);
    }

    private async Task WithPasswordAsync(
        Func<ReadOnlyMemory<char>, CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        char[]? password = null;
        if (_session?.SnapshotEncrypted == true)
        {
            password = _dialogs.RequestPassword(
                "암호화 Snapshot",
                "계속하려면 Snapshot 비밀번호를 다시 입력하세요. 비밀번호는 세션에 저장되지 않습니다.");
            if (password is null)
            {
                return;
            }
        }

        try
        {
            await RunAsync(token => operation(password ?? [], token), cancellationToken);
        }
        finally
        {
            if (password is not null)
            {
                Array.Clear(password);
            }
        }
    }

    private async Task RunAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        if (IsBusy)
        {
            return;
        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        _activeOperation = linked;
        IsBusy = true;
        try
        {
            await operation(linked.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText = "작업을 취소했습니다. 저장된 세션에서 안전하게 다시 시작할 수 있습니다.";
        }
        catch (ReplicaSnapshotDecryptionException)
        {
            StatusText = "Snapshot을 열 수 없습니다. 비밀번호 또는 파일 상태를 확인하세요.";
            _dialogs.ShowError("Recovery Snapshot", StatusText);
        }
        catch (ReplicaSnapshotException)
        {
            StatusText = "Snapshot이 손상되었거나 안전 요구사항을 충족하지 않습니다.";
            _dialogs.ShowError("Recovery Snapshot", StatusText);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            StatusText = "복구 단계를 안전하게 완료하지 못했습니다. 완료된 변경과 롤백 기록은 유지됩니다. 현재 단계의 입력을 확인한 뒤 다시 시도하세요.";
            _dialogs.ShowError("초기화 후 복구", StatusText);
        }
        finally
        {
            _activeOperation = null;
            IsBusy = false;
        }
    }

    private void Apply(RecoveryWizardSession session)
    {
        _session = session;
        SourceComputerText = $"{session.SourceMachineName} · Windows {session.SourceWindowsVersion} · " +
            $"{session.SourceArchitecture} · {session.SourceLocale}";
        CurrentStepText = $"{GetPhaseName(session.CurrentStep)} · {GetStepName(session.CurrentStep)}";
        ScoreText = $"복원 전 일치율: {FormatScore(session.SimilarityBefore)} · " +
            $"복원 후 일치율: {FormatScore(session.SimilarityAfter)}";

        Replace(PathMappings, session.PathMappings.Select(mapping => new RecoveryPathMappingRow(mapping)));
        Replace(HardwareDifferences, session.HardwareDifferences);
        Replace(ManualActions, session.ManualActions);
        Replace(ActionResults, session.ActionResults);
        Replace(PlanActions, session.Plan?.Actions ?? []);
        for (int index = 0; index < Steps.Count; index++)
        {
            Steps[index].State = index + 1 < (int)session.CurrentStep
                ? "완료"
                : index + 1 == (int)session.CurrentStep ? "현재" : "대기";
        }

        foreach (RecoveryPhaseRow phase in Phases)
        {
            phase.State = (int)session.CurrentStep > phase.EndStep
                ? "완료"
                : (int)session.CurrentStep >= phase.StartStep ? "현재" : "대기";
        }

        OnPropertyChanged(nameof(Plan));
        OnPropertyChanged(nameof(PlanSummary));
        OnPropertyChanged(nameof(CanAnalyze));
        OnPropertyChanged(nameof(CanApprovePlan));
        OnPropertyChanged(nameof(CanExecute));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanApproveRestart));
        OnPropertyChanged(nameof(CanConfirmResume));
        OnPropertyChanged(nameof(HasOfflineInstallers));
        ExportOfflineInstallersCommand.NotifyCanExecuteChanged();
        ResultText = BuildResultText(session);
    }

    private static string BuildResultText(RecoveryWizardSession session)
    {
        Dictionary<string, RestoreActionType> types = session.Plan?.Actions.ToDictionary(
            action => action.Id,
            action => action.Type,
            StringComparer.Ordinal) ?? [];
        int Succeeded(params RestoreActionType[] accepted) => session.ActionResults.Count(result =>
            result.State is RestoreExecutionState.Succeeded or RestoreExecutionState.RequiresRestart &&
            types.TryGetValue(result.ActionId, out RestoreActionType type) && accepted.Contains(type));
        int failed = session.ActionResults.Count(result => result.State == RestoreExecutionState.Failed);
        int skipped = session.ActionResults.Count(result => result.State == RestoreExecutionState.Skipped);
        return $"설치 성공 {Succeeded(RestoreActionType.InstallPackage, RestoreActionType.UpdatePackage)} · " +
            $"설정 복원 성공 {Succeeded(RestoreActionType.RestoreFile, RestoreActionType.MergeJson, RestoreActionType.SetUserEnvironmentVariable, RestoreActionType.SetMachineEnvironmentVariable, RestoreActionType.AddPathEntry, RestoreActionType.RestoreRegistryValue)} · " +
            $"파일 복원 성공 {Succeeded(RestoreActionType.RestoreSelectedUserFile)} · 실패 {failed} · " +
            $"건너뜀 {skipped} · 재부팅 필요 {(session.RequiresRestart ? "예" : "아니요")} · " +
            $"수동 작업 {session.ManualActions.Count}";
    }

    private static IEnumerable<RecoveryStepRow> CreateSteps() => Enum.GetValues<RecoveryWizardStep>()
        .Select(step => new RecoveryStepRow((int)step, GetStepName(step), "대기"));

    private static IEnumerable<RecoveryPhaseRow> CreatePhases() =>
    [
        new(1, "Snapshot 확인", 1, 4, "대기"),
        new(2, "현재 PC 비교", 5, 9, "대기"),
        new(3, "계획 검토", 10, 11, "대기"),
        new(4, "선택 작업 복원", 12, 16, "대기"),
        new(5, "결과 확인", 17, 19, "대기"),
    ];

    private static string GetPhaseName(RecoveryWizardStep step) => (int)step switch
    {
        <= 4 => "Snapshot 확인",
        <= 9 => "현재 PC 비교",
        <= 11 => "계획 검토",
        <= 16 => "선택 작업 복원",
        _ => "결과 확인",
    };

    private static string GetStepName(RecoveryWizardStep step) => step switch
    {
        RecoveryWizardStep.SelectSnapshot => "Recovery Snapshot 선택",
        RecoveryWizardStep.ValidateSnapshot => "Snapshot 무결성 확인",
        RecoveryWizardStep.EnterPassword => "비밀번호 입력",
        RecoveryWizardStep.ShowSourceComputer => "Snapshot 생성 PC 정보",
        RecoveryWizardStep.ScanCurrentComputer => "현재 PC 스캔",
        RecoveryWizardStep.CheckCompatibility => "하드웨어 및 Windows 호환성",
        RecoveryWizardStep.AnalyzeApplications => "프로그램 차이 분석",
        RecoveryWizardStep.AnalyzeSettings => "설정 차이 분석",
        RecoveryWizardStep.ConfirmFileDestinations => "사용자 파일 복원 위치",
        RecoveryWizardStep.CreateRestorePlan => "복원 계획 생성",
        RecoveryWizardStep.FinalApproval => "사용자 최종 승인",
        RecoveryWizardStep.InstallApplications => "프로그램 설치",
        RecoveryWizardStep.RestoreSettings => "설정 복원",
        RecoveryWizardStep.RestoreUserFiles => "사용자 파일 복원",
        RecoveryWizardStep.ShowRestartRequirements => "재부팅 필요 항목",
        RecoveryWizardStep.ResumeAfterRestart => "재부팅 후 Replica 재개",
        RecoveryWizardStep.FinalValidation => "최종 검증",
        RecoveryWizardStep.ShowSimilarity => "환경 일치율",
        _ => "수동 작업 목록",
    };

    private static string FormatScore(int? score) => score.HasValue ? $"{score.Value}%" : "-";

    private static string FormatBytes(long? bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(bytes ?? 0, 0);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }

    private bool CanExportOfflineInstallers() => !IsBusy &&
        _session is not null &&
        HasOfflineInstallers;

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (T item in source)
        {
            target.Add(item);
        }
    }
}

public sealed partial class RecoveryPhaseRow : ObservableObject
{
    [ObservableProperty]
    private string _state;

    public RecoveryPhaseRow(int number, string name, int startStep, int endStep, string state)
    {
        Number = number;
        Name = name;
        StartStep = startStep;
        EndStep = endStep;
        _state = state;
    }

    public int Number { get; }

    public string Name { get; }

    public int StartStep { get; }

    public int EndStep { get; }
}

public sealed partial class RecoveryStepRow : ObservableObject
{
    [ObservableProperty]
    private string _state;

    public RecoveryStepRow(int number, string name, string state)
    {
        Number = number;
        Name = name;
        _state = state;
    }

    public int Number { get; }

    public string Name { get; }
}

public sealed partial class RecoveryPathMappingRow : ObservableObject
{
    public static IReadOnlyList<RestoreFileConflictBehavior> AvailableConflictBehaviors { get; } =
        Enum.GetValues<RestoreFileConflictBehavior>();

    [ObservableProperty]
    private string _targetPath;

    [ObservableProperty]
    private RestoreFileConflictBehavior _conflictBehavior;

    public RecoveryPathMappingRow(RecoveryPathMapping mapping)
    {
        SourcePath = mapping.SourcePath;
        _targetPath = mapping.TargetPath;
        Scope = mapping.Scope;
        _conflictBehavior = mapping.ConflictBehavior;
        EstimatedBytes = mapping.EstimatedBytes;
    }

    public string SourcePath { get; }

    public RecoveryPathMappingScope Scope { get; }

    public long EstimatedBytes { get; }

    public IReadOnlyList<RestoreFileConflictBehavior> ConflictBehaviorOptions =>
        AvailableConflictBehaviors;

    public RecoveryPathMapping ToModel() => new(
        SourcePath,
        TargetPath,
        Scope,
        ConflictBehavior,
        EstimatedBytes);
}
