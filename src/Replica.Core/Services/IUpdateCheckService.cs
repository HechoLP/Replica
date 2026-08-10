using Replica.Core.Models;
using Replica.Core.Updates;

namespace Replica.Core.Services;

public interface IUpdateCheckService
{
    Task<UpdateCheckResult> CheckForUpdatesAsync(
        bool includePrerelease,
        CancellationToken cancellationToken);

    Task<UpdateCheckResult> CheckForUpdatesAsync(
        UpdateChannel channel,
        CancellationToken cancellationToken);
}
