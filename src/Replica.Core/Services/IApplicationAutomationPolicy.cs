using Replica.Core.Matching;

namespace Replica.Core.Services;

public interface IApplicationAutomationPolicy
{
    ApplicationAutomationDecision Evaluate(
        ApplicationMatchConfidence confidence,
        ApplicationVersionComparison versionComparison);
}
