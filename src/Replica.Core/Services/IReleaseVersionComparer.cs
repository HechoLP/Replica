namespace Replica.Core.Services;

public interface IReleaseVersionComparer
{
    bool IsNewer(Version candidate, Version current);
}
