using Replica.App.ViewModels;
using Replica.Core.Diffing;
using Replica.Core.Planning;

namespace Replica.IntegrationTests;

public sealed class RestoreDryRunViewModelTests
{
    [Fact]
    public void Display_ShowsDryRunSummaryAndActionSafetyDetails()
    {
        RestoreDryRunViewModel viewModel = new();

        viewModel.Display(CreatePlan());

        Assert.True(viewModel.CanReview);
        Assert.Contains(
            viewModel.Summary,
            row => row.Name == "설치 프로그램" && row.Value == "1");
        Assert.Contains(
            viewModel.Summary,
            row => row.Name == "예상 다운로드" && row.Value == "50 MB");
        RestoreActionRowViewModel row = Assert.Single(viewModel.Actions);
        Assert.Equal("프로그램 설치", row.Type);
        Assert.Equal("필요", row.Administrator);
        Assert.Equal("필요", row.Restart);
        Assert.Equal("불가", row.Rollback);
        Assert.Equal("선택", row.Selection);
        Assert.Equal("자동화 지원", row.Handling);
    }

    [Fact]
    public void ConfirmPlan_RecordsApprovalWithoutExecutingChanges()
    {
        RestoreDryRunViewModel viewModel = new();
        viewModel.Display(CreatePlan());

        viewModel.ConfirmPlanCommand.Execute(null);

        Assert.NotNull(viewModel.ReviewedPlan);
        Assert.True(viewModel.ReviewedPlan.IsApproved);
        Assert.False(viewModel.CanReview);
        Assert.Contains("시스템 변경은 실행되지 않았습니다", viewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectPlan_RecordsRejectionWithoutExecutingChanges()
    {
        RestoreDryRunViewModel viewModel = new();
        viewModel.Display(CreatePlan());

        viewModel.RejectPlanCommand.Execute(null);

        Assert.NotNull(viewModel.ReviewedPlan);
        Assert.Equal(RestorePlanReviewStatus.Rejected, viewModel.ReviewedPlan.ReviewStatus);
        Assert.False(viewModel.CanReview);
        Assert.Contains("시스템은 변경되지 않았습니다", viewModel.StatusText, StringComparison.Ordinal);
    }

    private static RestorePlan CreatePlan()
    {
        RestoreAction action = new(
            "action-install",
            RestoreActionType.InstallPackage,
            "Git",
            "Install Git from an allow-listed package source.",
            "2.51.0",
            null,
            "2.51.0",
            DiffRiskLevel.Medium,
            true,
            true,
            false,
            [],
            TimeSpan.FromMinutes(2),
            52_428_800,
            true,
            false,
            DiffArea.Applications,
            "Git.Git",
            "MissingApplicationCanInstall");
        RestoreDryRunSummary summary = new(
            1,
            0,
            0,
            0,
            0,
            1,
            true,
            0,
            0,
            1,
            TimeSpan.FromMinutes(2),
            52_428_800);
        return new RestorePlan(
            "plan-test",
            DiffRestoreMode.Safe,
            [action],
            summary,
            RestorePlanReviewStatus.PendingReview,
            DateTimeOffset.UtcNow);
    }
}
