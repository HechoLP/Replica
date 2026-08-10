using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Replica.Core.History;
using Replica.Core.Rollback;
using Replica.Core.Services;

namespace Replica.App.ViewModels;

public sealed partial class RollbackViewModel : ObservableObject
{
    private readonly IRollbackService _rollbackService;
    private readonly ISnapshotHistoryService? _snapshotHistory;
    private RollbackPlan? _plan;

    public RollbackViewModel(
        IRollbackService rollbackService,
        ISnapshotHistoryService? snapshotHistory = null)
    {
        _rollbackService = rollbackService;
        _snapshotHistory = snapshotHistory;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewRollbackCommand))]
    private RollbackSessionRowViewModel? _selectedSession;

    [ObservableProperty]
    private IReadOnlyList<RollbackSessionRowViewModel> _sessions = [];

    [ObservableProperty]
    private IReadOnlyList<RollbackItemRowViewModel> _items = [];

    [ObservableProperty]
    private IReadOnlyList<RollbackResultRowViewModel> _results = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshSessionsCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewRollbackCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExecuteRollbackCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "최근 복원 세션을 불러오면 롤백 가능한 변경을 미리 볼 수 있습니다.";

    public bool CanApprove => _plan?.ReviewStatus == RollbackPlanReviewStatus.PendingReview &&
        _plan.Items.Any(item => item.IsSelected);

    public bool CanExecute => _plan?.ReviewStatus == RollbackPlanReviewStatus.Approved && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshSessionsAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            IReadOnlyList<RollbackSessionSummary> sessions = await _rollbackService
                .GetRecentSessionsAsync(20, cancellationToken);
            Sessions = sessions.Select(session => new RollbackSessionRowViewModel(
                session.SessionId,
                session.UpdatedAtUtc.ToLocalTime().ToString("g"),
                session.State.ToString(),
                session.ItemCount,
                session.AutomaticItemCount,
                session.ManualItemCount)).ToArray();
            SelectedSession = Sessions.FirstOrDefault();
            StatusText = Sessions.Count == 0
                ? "롤백 가능한 최근 복원 세션이 없습니다."
                : $"최근 복원 세션 {Sessions.Count:N0}개를 불러왔습니다.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "최근 복원 세션 조회가 취소되었습니다.";
        }
        catch (Exception)
        {
            StatusText = "최근 복원 세션을 안전하게 읽지 못했습니다.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRefresh() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanPreview))]
    private async Task PreviewRollbackAsync(CancellationToken cancellationToken)
    {
        if (SelectedSession is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            _plan = await _rollbackService.CreatePlanAsync(
                SelectedSession.SessionId,
                null,
                cancellationToken);
            Items = _plan.Items.Select(item => new RollbackItemRowViewModel(
                item.ActionId,
                item.Name,
                item.Kind.ToString(),
                item.State.ToString(),
                item.CanRollbackAutomatically ? "가능" : "수동 조치",
                item.RequiresAdministrator ? "필요" : "불필요",
                item.IsSelected ? "선택" : "미선택",
                item.ManualInstruction ?? string.Empty)).ToArray();
            Results = [];
            StatusText = $"롤백 미리보기: 자동 {Items.Count(item => item.Selection == "선택"):N0}개, " +
                $"수동 {Items.Count(item => item.Automatic == "수동 조치"):N0}개. 실행 전에 승인하세요.";
            NotifyReviewStateChanged();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _plan = null;
            Items = [];
            StatusText = "Journal 무결성 검증에 실패하여 롤백 미리보기를 만들지 못했습니다.";
            NotifyReviewStateChanged();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanPreview() => SelectedSession is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanApprovePlan))]
    private void ApproveRollback()
    {
        if (_plan is null)
        {
            return;
        }

        _plan = _plan.Approve();
        StatusText = "롤백 계획을 승인했습니다. 실행하면 Replica가 기록한 변경만 되돌립니다.";
        NotifyReviewStateChanged();
    }

    private bool CanApprovePlan() => CanApprove;

    [RelayCommand(CanExecute = nameof(CanExecutePlan))]
    private async Task ExecuteRollbackAsync(CancellationToken cancellationToken)
    {
        if (_plan is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            Progress<RollbackProgress> progress = new(update =>
            {
                StatusText = $"롤백 중: {update.Name} ({update.CompletedItems}/{update.TotalItems})";
            });
            RollbackExecutionResult result = await _rollbackService.ExecuteAsync(
                _plan,
                progress,
                cancellationToken);
            await TryRecordHistoryAsync(result, cancellationToken);
            Results = result.Items.Select(item => new RollbackResultRowViewModel(
                item.ActionId,
                item.State.ToString(),
                item.ReasonCode,
                item.Message)).ToArray();
            StatusText = result.WasAlreadyRolledBack
                ? "이미 완료된 롤백 세션입니다. 중복 변경은 수행하지 않았습니다."
                : result.WasPartial
                    ? "롤백이 일부 완료되었습니다. 실패 또는 미선택 항목을 검토하세요."
                    : "선택한 Replica 변경을 롤백하고 검증했습니다.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "롤백 실행이 취소되었습니다. 완료된 항목의 기록은 유지됩니다.";
        }
        catch (Exception)
        {
            StatusText = "롤백을 안전하게 실행하지 못했습니다. Journal과 수동 조치를 검토하세요.";
        }
        finally
        {
            IsBusy = false;
            NotifyReviewStateChanged();
        }
    }

    private bool CanExecutePlan() => CanExecute;

    private async Task TryRecordHistoryAsync(
        RollbackExecutionResult result,
        CancellationToken cancellationToken)
    {
        if (_snapshotHistory is null)
        {
            return;
        }

        try
        {
            await _snapshotHistory.RecordRollbackAsync(
                new RollbackHistoryRecord(
                    result.SessionId,
                    DateTimeOffset.UtcNow,
                    result.State.ToString(),
                    result.Items.Count,
                    result.Items.Count(item => item.State == RollbackJournalState.RollbackFailed)),
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException and not OutOfMemoryException)
        {
            // The verified rollback result remains authoritative if local history indexing fails.
        }
    }

    private void NotifyReviewStateChanged()
    {
        OnPropertyChanged(nameof(CanApprove));
        OnPropertyChanged(nameof(CanExecute));
        ApproveRollbackCommand.NotifyCanExecuteChanged();
        ExecuteRollbackCommand.NotifyCanExecuteChanged();
    }
}

public sealed record RollbackSessionRowViewModel(
    string SessionId,
    string UpdatedAt,
    string State,
    int ItemCount,
    int AutomaticCount,
    int ManualCount);

public sealed record RollbackItemRowViewModel(
    string ActionId,
    string Name,
    string Kind,
    string State,
    string Automatic,
    string Administrator,
    string Selection,
    string ManualInstruction);

public sealed record RollbackResultRowViewModel(
    string ActionId,
    string State,
    string ReasonCode,
    string Message);
