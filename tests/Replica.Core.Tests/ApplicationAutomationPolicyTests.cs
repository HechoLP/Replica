using Replica.Core.Matching;

namespace Replica.Core.Tests;

public sealed class ApplicationAutomationPolicyTests
{
    private readonly ApplicationAutomationPolicy _policy = new();

    [Theory]
    [InlineData(ApplicationMatchConfidence.Low)]
    [InlineData(ApplicationMatchConfidence.Unknown)]
    public void Evaluate_LowOrUnknownIdentity_BlocksAutomaticActions(
        ApplicationMatchConfidence confidence)
    {
        ApplicationAutomationDecision decision = _policy.Evaluate(
            confidence,
            ApplicationVersionComparison.SourceNewer);

        Assert.False(decision.CanAutomaticallyInstall);
        Assert.False(decision.CanAutomaticallyUpdate);
        Assert.False(decision.CanAutomaticallyDowngrade);
    }

    [Fact]
    public void Evaluate_ReliableIdentityAndNewerSource_AllowsUpdateButNeverDowngrade()
    {
        ApplicationAutomationDecision decision = _policy.Evaluate(
            ApplicationMatchConfidence.Exact,
            ApplicationVersionComparison.SourceNewer);

        Assert.True(decision.CanAutomaticallyInstall);
        Assert.True(decision.CanAutomaticallyUpdate);
        Assert.False(decision.CanAutomaticallyDowngrade);
    }

    [Theory]
    [InlineData(ApplicationVersionComparison.TargetNewer)]
    [InlineData(ApplicationVersionComparison.Incomparable)]
    [InlineData(ApplicationVersionComparison.Unknown)]
    public void Evaluate_UnsafeVersionResult_BlocksAutomaticVersionChange(
        ApplicationVersionComparison comparison)
    {
        ApplicationAutomationDecision decision = _policy.Evaluate(
            ApplicationMatchConfidence.High,
            comparison);

        Assert.False(decision.CanAutomaticallyUpdate);
        Assert.False(decision.CanAutomaticallyDowngrade);
    }
}
