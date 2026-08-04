using Replica.Core.Models;

namespace Replica.Core.Services;

public interface IReleaseProvider
{
    Task<ReleaseInfo?> GetLatestReleaseAsync(
        bool includePrerelease,
        CancellationToken cancellationToken);
}
