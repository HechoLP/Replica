using Replica.Core.Models;

namespace Replica.Core.Services;

public interface IUpdateCheckService
{
    Task<UpdateCheckResult> CheckForUpdatesAsync(
        bool includePrerelease,
        CancellationToken cancellationToken);
}
