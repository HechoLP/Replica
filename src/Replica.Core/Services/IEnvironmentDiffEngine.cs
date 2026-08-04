using Replica.Core.Diffing;

namespace Replica.Core.Services;

public interface IEnvironmentDiffEngine
{
    EnvironmentDiffResult Compare(
        DiffEnvironmentState source,
        DiffEnvironmentState target,
        DiffRestoreMode mode,
        CancellationToken cancellationToken = default);
}
