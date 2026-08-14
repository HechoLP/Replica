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

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
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
    private string _currentStepText = "1 / 19 · Recovery Snapshot 선택";

    public RecoveryWizardViewModel(
        IRecoveryWizardService wizard,
        IRecoveryDialogService dialogs)
    {
        _wizard = wizard;
        _dialogs = dialogs;
        Steps = new ObservableCollection<RecoveryStepRow>(CreateSteps());
    }

    public ObservableCollection<RecoveryStepRow> Steps { get; }

    public ObservableCollection<RecoveryPathMappingRow> PathMappings { get; } = [];

    public ObservableCollection<RecoveryHardwareDifference> HardwareDifferences { get; } = [];

    public ObservableCollection<RecoveryManualAction> ManualActions { get; } = [];

    public ObservableCollection<RestoreActionExecutionResult> ActionResults { get; } = [];

    public ObservableCollection<RestoreAction> PlanActions { get; } = [];

    public RestorePlan? Plan => _session?.Plan;

    public bool CanAnalyze => _session is
    {
        Status: RecoveryWizardStatus.InProgress,
        CurrentStep: RecoveryWizardStep.ShowSourceComputer,
    };

    public bool CanApprovePlan => _session?.Status == RecoveryWizardStatus.AwaitingApproval;

    public bool CanExecute => _session is { Status: RecoveryWizardStatus.InProgress, Plan.IsApproved: true };

    public bool CanRetry => _session?.CanRetry == true;

    public bool CanApproveRestart => _session?.Status == RecoveryWizardStatus.AwaitingRestartApproval;

    public bool CanConfirmResume => _session?.Status == RecoveryWizardStatus.AwaitingResumeConfirmation;

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

        await RunAsync(async () =>
        {
            RecoveryStartResult started = await _wizard.StartAsync(
                path,
                ReadOnlyMemory<char>.Empty,
                cancellationToken);
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
                    started = await _wizard.StartAsync(path, password, cancellationToken);
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
        });
    }

    public async Task LoadResumeAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await RunAsync(async () =>
        {
            RecoveryWizardSession? session = await _wizard.LoadForResumeAsync(
                sessionId,
                cancellationToken);
            if (session is null)
            {
                return;
            }

            IsVisible = true;
            Apply(session);
            StatusText = "재부팅 전 복구 상태를 확인했습니다. 계속을 선택해야 최종 검증을 시작합니다.";
        });
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task AnalyzeAsync(CancellationToken cancellationToken)
    {
        if (_session is null)
        {
            return;
        }

        await WithPasswordAsync(async password =>
        {
            RecoveryWizardSession analyzed = await _wizard.AnalyzeAsync(
                _session.SessionId,
                PathMappings.Select(row => row.ToModel()).ToArray(),
                password,
                cancellationToken);
            Apply(analyzed);
            StatusText = analyzed.FailureReasonCode == "InsufficientStorage"
                ? "대상 드라이브의 저장 공간이 부족합니다. 경로 매핑을 변경하세요."
                : "차이 분석과 복원 계획을 만들었습니다. 계획을 검토하고 최종 승인하세요.";
        });
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

        await RunAsync(async () =>
        {
            Apply(await _wizard.ApprovePlanAsync(_session.SessionId, cancellationToken));
            StatusText = "복원 계획이 승인되었습니다. 실행 전 요약을 다시 확인하세요.";
        });
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

        await RunAsync(async () =>
        {
            Apply(await _wizard.ApproveRestartAsync(_session.SessionId, true, cancellationToken));
            StatusText = "로그인 후 재개가 등록되었습니다. 준비가 되면 Windows를 직접 재부팅하세요.";
        });
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

        await WithPasswordAsync(async password =>
        {
            Apply(await _wizard.ConfirmResumeAsync(
                _session.SessionId,
                password,
                cancellationToken));
            StatusText = "재부팅 후 검증을 완료했습니다.";
        });
    }

    [RelayCommand]
    private async Task CancelAsync(CancellationToken cancellationToken)
    {
        if (_session is null)
        {
            IsVisible = false;
            return;
        }

        await RunAsync(async () =>
        {
            Apply(await _wizard.CancelAsync(_session.SessionId, cancellationToken));
            StatusText = "복구를 취소했습니다. 완료된 변경은 롤백 화면에서 검토할 수 있습니다.";
        });
    }

    private Task ExecuteWithPasswordAsync(bool retry, CancellationToken cancellationToken)
    {
        if (_session is null)
        {
            return Task.CompletedTask;
        }

        return WithPasswordAsync(async password =>
        {
            RecoveryWizardSession result = retry
                ? await _wizard.RetryFailedAsync(_session.SessionId, password, cancellationToken)
                : await _wizard.ExecuteAsync(_session.SessionId, password, cancellationToken);
            Apply(result);
            StatusText = result.Status switch
            {
                RecoveryWizardStatus.AwaitingRestartApproval =>
                    "선택된 작업을 마쳤으며 재부팅이 필요합니다. 자동 재부팅은 하지 않습니다.",
                RecoveryWizardStatus.Completed => "복원과 최종 검증을 완료했습니다.",
                RecoveryWizardStatus.Cancelled => "실행을 취소했습니다.",
                _ when result.CanRetry => "일부 작업이 실패했습니다. 실패한 작업만 다시 시도할 수 있습니다.",
                _ => "복원 실행 결과를 확인하세요.",
            };
        });
    }

    private async Task WithPasswordAsync(Func<ReadOnlyMemory<char>, Task> operation)
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
            await RunAsync(() => operation(password ?? []));
        }
        finally
        {
            if (password is not null)
            {
                Array.Clear(password);
            }
        }
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
        catch (Exception exception)
        {
            StatusText = $"복구 단계를 완료하지 못했습니다. ({exception.GetType().Name})";
            _dialogs.ShowError("초기화 후 복구", StatusText);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Apply(RecoveryWizardSession session)
    {
        _session = session;
        SourceComputerText = $"{session.SourceMachineName} · Windows {session.SourceWindowsVersion} · " +
            $"{session.SourceArchitecture} · {session.SourceLocale}";
        CurrentStepText = $"{(int)session.CurrentStep} / 19 · {GetStepName(session.CurrentStep)}";
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

        OnPropertyChanged(nameof(Plan));
        OnPropertyChanged(nameof(PlanSummary));
        OnPropertyChanged(nameof(CanAnalyze));
        OnPropertyChanged(nameof(CanApprovePlan));
        OnPropertyChanged(nameof(CanExecute));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanApproveRestart));
        OnPropertyChanged(nameof(CanConfirmResume));
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

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (T item in source)
        {
            target.Add(item);
        }
    }
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
