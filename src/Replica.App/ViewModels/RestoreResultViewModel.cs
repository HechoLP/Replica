using CommunityToolkit.Mvvm.ComponentModel;
using Replica.Core.Execution;

namespace Replica.App.ViewModels;

public sealed partial class RestoreResultViewModel : ObservableObject
{
    [ObservableProperty]
    private string _beforeScore = "—";

    [ObservableProperty]
    private string _afterScore = "—";

    [ObservableProperty]
    private int _succeededCount;

    [ObservableProperty]
    private int _failedCount;

    [ObservableProperty]
    private int _skippedCount;

    [ObservableProperty]
    private int _manualActionCount;

    [ObservableProperty]
    private bool _requiresRestart;

    [ObservableProperty]
    private string _statusText = "복원 결과가 아직 없습니다.";

    [ObservableProperty]
    private IReadOnlyList<RestoreResultRowViewModel> _items = [];

    public void Display(RestoreExecutionResult result, int? beforeScore = null, int? afterScore = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        BeforeScore = beforeScore is int before ? $"{before}%" : "측정 전";
        AfterScore = afterScore is int after ? $"{after}%" : "최종 검증 필요";
        SucceededCount = result.Actions.Count(action =>
            action.State is RestoreExecutionState.Succeeded or RestoreExecutionState.RequiresRestart);
        FailedCount = result.Actions.Count(action => action.State == RestoreExecutionState.Failed);
        SkippedCount = result.Actions.Count(action => action.State == RestoreExecutionState.Skipped);
        ManualActionCount = result.Actions.Count(action =>
            action.ReasonCode.Equals("ManualActionRequired", StringComparison.Ordinal));
        RequiresRestart = result.RequiresRestart;
        Items = result.Actions.Select(action => new RestoreResultRowViewModel(
            action.ActionId,
            GetStateName(action.State),
            ReasonCodePresenter.GetText(action.ReasonCode),
            action.Message,
            action.RequiresRestart ? "필요" : "불필요")).ToArray();
        StatusText = result.WasCancelled
            ? "사용자 취소로 종료했습니다. 완료된 작업은 결과와 Rollback 기록에 남아 있습니다."
            : FailedCount > 0
                ? "일부 작업이 실패했습니다. 실패와 수동 작업을 검토하고 필요한 경우 롤백하세요."
                : "선택한 복원 작업을 완료했습니다. 최종 검증과 수동 작업을 확인하세요.";
    }

    private static string GetStateName(RestoreExecutionState state) => state switch
    {
        RestoreExecutionState.Succeeded => "성공",
        RestoreExecutionState.Failed => "실패",
        RestoreExecutionState.Skipped => "건너뜀",
        RestoreExecutionState.Cancelled => "취소",
        RestoreExecutionState.RequiresRestart => "재부팅 필요",
        RestoreExecutionState.RolledBack => "롤백됨",
        RestoreExecutionState.Running => "실행 중",
        _ => "대기",
    };
}

public sealed record RestoreResultRowViewModel(
    string ActionId,
    string State,
    string Reason,
    string Message,
    string Restart);
