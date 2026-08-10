using Replica.Core.Updates;

namespace Replica.Core.Models;

public enum UpdateCheckStatus
{
    UpToDate,
    UpdateAvailable,
    Skipped,
    RateLimited,
    NetworkUnavailable,
    TimedOut,
    InvalidResponse,
    AssetMissing,
    Unavailable,
}

public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    Version CurrentVersion,
    GitHubReleaseDetails? LatestRelease,
    UpdateChannel Channel = UpdateChannel.Stable,
    DateTimeOffset? RetryAtUtc = null);
