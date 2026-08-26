using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Replica.App.Services;
using Replica.Core.Execution;
using Replica.Core.Planning;
using Replica.Core.Services;
using Replica.Infrastructure.Restore;

namespace Replica.App.ViewModels;

public sealed partial class RestoreExecutionViewModel : ObservableObject
{
    private readonly IRecoveryDialogService dialogs;
    private readonly IRestoreExecutor executor;
    private readonly IRestoreJournal journal;
    private readonly RestoreDryRunViewModel restorePlan;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _isFinalApprovalChecked;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _canCancel;

    [ObservableProperty]
    private int _progressPercentage;

    [ObservableProperty]
    private string _currentStage = "실행 대기";

    [ObservableProperty]
    private string _currentAction = "—";

    [ObservableProperty]
    private string _statusText = "승인된 Restore Plan이 있어야 실행할 수 있습니다.";

    [ObservableProperty]
    private IReadOnlyList<ExecutionLogRowViewModel> _logSummary = [];

    public RestoreExecutionViewModel(
        IRestoreExecutor executor,
        IRestoreJournal journal,
        IRecoveryDialogService dialogs,
        RestoreDryRunViewModel restorePlan,
        RestoreResultViewModel result)
    {
        this.executor = executor;
        this.journal = journal;
        this.dialogs = dialogs;
        this.restorePlan = restorePlan;
        Result = result;
    }

    public RestoreResultViewModel Result { get; }

    public void Prepare()
    {
        RestorePlan? plan = restorePlan.ReviewedPlan;
        StatusText = plan?.IsApproved == true
            ? $"승인된 {plan.Mode} Plan · 선택 작업 {plan.DryRun.SelectedActionCount:N0}개"
            : "Restore Plan 화면에서 Dry Run을 검토하고 승인해야 합니다.";
        IsFinalApprovalChecked = false;
        ProgressPercentage = 0;
        LogSummary = [];
        StartCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanStart), IncludeCancelCommand = true)]
    private async Task StartAsync(CancellationToken cancellationToken)
    {
        RestorePlan? plan = restorePlan.ReviewedPlan;
        if (plan is null || !plan.IsApproved || !dialogs.Confirm(
                "복원 실행 최종 승인",
                "검토한 Restore Plan의 선택 작업만 실행합니다. 변경 전 Rollback Journal을 기록하며 자동 재부팅하지 않습니다. 계속할까요?"))
        {
            return;
        }

        IsBusy = true;
        CanCancel = true;
        string sessionId = Guid.NewGuid().ToString("N");
        UiRestoreProgressReporter reporter = new(Dispatcher.CurrentDispatcher, progress =>
        {
            CurrentStage = GetStateName(progress.State);
            CurrentAction = progress.ActionName;
            ProgressPercentage = progress.TotalActions == 0
                ? 0
                : (int)Math.Clamp(progress.CompletedActions * 100 / progress.TotalActions, 0, 100);
            LogSummary =
            [
                .. LogSummary.TakeLast(49),
                new ExecutionLogRowViewModel(
                    DateTime.Now.ToString("T"),
                    progress.ActionName,
                    GetStateName(progress.State),
                    progress.Message),
            ];
        });
        try
        {
            RestoreExecutionResult result = await executor.ExecuteAsync(
                plan,
                new RestoreExecutionContext(sessionId, false, journal),
                reporter,
                cancellationToken);
            Result.Display(result);
            ProgressPercentage = 100;
            StatusText = result.WasCancelled
                ? "복원이 취소되었습니다. 완료된 작업과 Journal은 유지됩니다."
                : "복원 실행이 끝났습니다. 결과와 수동 작업을 검토하세요.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "복원 취소를 요청했습니다. 현재 안전 경계가 끝난 뒤 중지됩니다.";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            StatusText = "복원을 안전하게 완료하지 못했습니다. 완료된 작업은 Journal에 기록되며, 결과 화면에서 실패 항목과 롤백 가능 여부를 확인하세요.";
        }
        finally
        {
            IsBusy = false;
            CanCancel = false;
            IsFinalApprovalChecked = false;
        }
    }

    [RelayCommand]
    private void RetryFailed() => StatusText =
        "실패 작업은 새 Restore Plan에서 다시 검토하고 승인해야 합니다. 자동 재시도하지 않습니다.";

    private bool CanStart() => !IsBusy &&
        IsFinalApprovalChecked &&
        restorePlan.ReviewedPlan?.IsApproved == true;

    private static string GetStateName(RestoreExecutionState state) => state switch
    {
        RestoreExecutionState.Pending => "대기",
        RestoreExecutionState.Running => "실행 중",
        RestoreExecutionState.Succeeded => "성공",
        RestoreExecutionState.Failed => "실패",
        RestoreExecutionState.Skipped => "건너뜀",
        RestoreExecutionState.Cancelled => "취소",
        RestoreExecutionState.RequiresRestart => "재부팅 필요",
        _ => "롤백됨",
    };

    private sealed class UiRestoreProgressReporter(
        Dispatcher dispatcher,
        Action<RestoreProgress> report) : IRestoreProgressReporter
    {
        public async Task ReportAsync(RestoreProgress progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (dispatcher.CheckAccess())
            {
                report(progress);
                return;
            }

            await dispatcher.InvokeAsync(
                () => report(progress),
                DispatcherPriority.DataBind,
                cancellationToken);
        }
    }
}

public sealed record ExecutionLogRowViewModel(
    string Time,
    string Action,
    string State,
    string Message);
