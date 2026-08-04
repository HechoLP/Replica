using Replica.Core.Diffing;
using Replica.Core.Planning;

namespace Replica.Core.Services;

public interface IRestorePlanner
{
    RestorePlan CreatePlan(
        EnvironmentDiffResult diff,
        RestorePlanningOptions? options = null,
        CancellationToken cancellationToken = default);
}
