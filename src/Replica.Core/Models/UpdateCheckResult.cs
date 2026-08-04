namespace Replica.Core.Models;

public enum UpdateCheckStatus
{
    UpToDate,
    UpdateAvailable,
    Unavailable,
}

public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    Version CurrentVersion,
    ReleaseInfo? LatestRelease);
