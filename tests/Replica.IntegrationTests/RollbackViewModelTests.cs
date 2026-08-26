using Replica.App.ViewModels;
using Replica.Core.Rollback;
using Replica.Core.Services;

namespace Replica.IntegrationTests;

public sealed class RollbackViewModelTests
{
    [Fact]
    public async Task Commands_LoadPreviewRequireApprovalAndShowResult()
    {
        FakeRollbackService service = new();
        RollbackViewModel viewModel = new(service);

        await viewModel.RefreshSessionsCommand.ExecuteAsync(null);
        await viewModel.PreviewRollbackCommand.ExecuteAsync(null);

        Assert.Single(viewModel.Sessions);
        Assert.Equal(2, viewModel.Items.Count);
        Assert.True(viewModel.CanApprove);
        Assert.False(viewModel.CanExecute);
        Assert.Contains(viewModel.Items, item => item.Automatic == "가능");
        Assert.Contains(viewModel.Items, item => item.Automatic == "수동 조치");

        viewModel.ApproveRollbackCommand.Execute(null);
        Assert.True(viewModel.CanExecute);

        await viewModel.ExecuteRollbackCommand.ExecuteAsync(null);

        Assert.Equal(1, service.ExecutionCount);
        Assert.Single(viewModel.Results);
        Assert.Contains("롤백", viewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectingAnotherSessionInvalidatesPreviewAndApproval()
    {
        FakeRollbackService service = new() { IncludeSecondSession = true };
        RollbackViewModel viewModel = new(service);
        await viewModel.RefreshSessionsCommand.ExecuteAsync(null);
        await viewModel.PreviewRollbackCommand.ExecuteAsync(null);
        viewModel.ApproveRollbackCommand.Execute(null);
        Assert.True(viewModel.CanExecute);

        viewModel.SelectedSession = viewModel.Sessions[1];

        Assert.Empty(viewModel.Items);
        Assert.False(viewModel.CanApprove);
        Assert.False(viewModel.CanExecute);
        Assert.False(viewModel.ExecuteRollbackCommand.CanExecute(null));
    }

    private sealed class FakeRollbackService : IRollbackService
    {
        public int ExecutionCount { get; private set; }

        public bool IncludeSecondSession { get; init; }

        public Task<IReadOnlyList<RollbackSessionSummary>> GetRecentSessionsAsync(
            int maximumCount,
            CancellationToken cancellationToken)
        {
            List<RollbackSessionSummary> sessions =
            [
                new(
                    "session-ui",
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    RollbackJournalState.Verified,
                    2,
                    1,
                    1),
            ];
            if (IncludeSecondSession)
            {
                sessions.Add(new(
                    "session-ui-2",
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    RollbackJournalState.Verified,
                    1,
                    1,
                    0));
            }

            return Task.FromResult<IReadOnlyList<RollbackSessionSummary>>(sessions);
        }

        public Task<RollbackPlan> CreatePlanAsync(
            string sessionId,
            IReadOnlyCollection<string>? selectedActionIds,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new RollbackPlan(
                sessionId,
                [
                    new RollbackPreviewItem(
                        "file",
                        "settings.json",
                        RollbackItemKind.File,
                        RollbackJournalState.Verified,
                        true,
                        false,
                        true,
                        null),
                    new RollbackPreviewItem(
                        "package",
                        "Git",
                        RollbackItemKind.ApplicationInstallation,
                        RollbackJournalState.Verified,
                        false,
                        false,
                        false,
                        "Remove manually if desired."),
                ],
                RollbackPlanReviewStatus.PendingReview,
                DateTimeOffset.UtcNow));
        }

        public Task<RollbackExecutionResult> ExecuteAsync(
            RollbackPlan approvedPlan,
            IProgress<RollbackProgress>? progress,
            CancellationToken cancellationToken)
        {
            Assert.Equal(RollbackPlanReviewStatus.Approved, approvedPlan.ReviewStatus);
            ExecutionCount++;
            return Task.FromResult(new RollbackExecutionResult(
                approvedPlan.SessionId,
                RollbackJournalState.RolledBack,
                [
                    new RollbackItemResult(
                        "file",
                        RollbackJournalState.RolledBack,
                        "RollbackVerified",
                        "Rolled back."),
                ],
                false,
                false));
        }
    }
}
