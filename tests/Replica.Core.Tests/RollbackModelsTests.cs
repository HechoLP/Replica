using Replica.Core.Rollback;

namespace Replica.Core.Tests;

public sealed class RollbackModelsTests
{
    [Fact]
    public void JournalStates_MatchRollbackContract()
    {
        Assert.Equal(
            [
                "Prepared",
                "Applied",
                "Verified",
                "RollbackPending",
                "RolledBack",
                "RollbackFailed",
            ],
            Enum.GetNames<RollbackJournalState>());
    }

    [Fact]
    public void RollbackPlan_RequiresExplicitReviewTransition()
    {
        RollbackPlan pending = new(
            "session",
            [],
            RollbackPlanReviewStatus.PendingReview,
            DateTimeOffset.UtcNow);

        Assert.Equal(RollbackPlanReviewStatus.Approved, pending.Approve().ReviewStatus);
        Assert.Equal(RollbackPlanReviewStatus.Rejected, pending.Reject().ReviewStatus);
    }
}
