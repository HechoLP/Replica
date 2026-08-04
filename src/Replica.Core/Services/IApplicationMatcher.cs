using Replica.Core.Matching;

namespace Replica.Core.Services;

public interface IApplicationMatcher
{
    ApplicationMatch Evaluate(
        ApplicationDescriptor source,
        ApplicationDescriptor target);

    ApplicationMatchingResult Match(
        IReadOnlyList<ApplicationDescriptor> source,
        IReadOnlyList<ApplicationDescriptor> target);

    ApplicationMatchConfidence AssessIdentityConfidence(ApplicationDescriptor application);
}
