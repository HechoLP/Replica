using Replica.Core.Services;

namespace Replica.Infrastructure.Updates;

public sealed class ReleaseVersionComparer : IReleaseVersionComparer
{
    public bool IsNewer(Version candidate, Version current)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(current);

        return candidate > current;
    }
}
