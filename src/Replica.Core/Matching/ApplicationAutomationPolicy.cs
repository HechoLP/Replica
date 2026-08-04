using Replica.Core.Services;

namespace Replica.Core.Matching;

public sealed class ApplicationAutomationPolicy : IApplicationAutomationPolicy
{
    public ApplicationAutomationDecision Evaluate(
        ApplicationMatchConfidence confidence,
        ApplicationVersionComparison versionComparison)
    {
        bool reliableIdentity = confidence is
            ApplicationMatchConfidence.Exact or
            ApplicationMatchConfidence.High or
            ApplicationMatchConfidence.Medium;
        if (!reliableIdentity)
        {
            return new ApplicationAutomationDecision(
                false,
                false,
                false,
                confidence == ApplicationMatchConfidence.Low
                    ? "LowConfidenceRequiresReview"
                    : "UnknownIdentityRequiresReview");
        }

        return versionComparison switch
        {
            ApplicationVersionComparison.SourceNewer => new ApplicationAutomationDecision(
                true,
                true,
                false,
                "ReliableIdentityAndSourceNewer"),
            ApplicationVersionComparison.TargetNewer => new ApplicationAutomationDecision(
                true,
                false,
                false,
                "AutomaticDowngradeBlocked"),
            ApplicationVersionComparison.Incomparable => new ApplicationAutomationDecision(
                true,
                false,
                false,
                "IncomparableVersionRequiresReview"),
            ApplicationVersionComparison.Unknown => new ApplicationAutomationDecision(
                true,
                false,
                false,
                "UnknownVersionRequiresReview"),
            _ => new ApplicationAutomationDecision(
                true,
                false,
                false,
                "NoVersionChangeRequired"),
        };
    }
}
