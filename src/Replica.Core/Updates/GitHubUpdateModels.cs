namespace Replica.Core.Updates;

public sealed record GitHubReleaseAssetInfo(
    string Name,
    Uri DownloadUri,
    long Size,
    string? Sha256);

public sealed record GitHubReleaseDetails(
    SemanticVersion Version,
    string TagName,
    string ReleaseName,
    DateTimeOffset PublishedAtUtc,
    bool IsPrerelease,
    bool IsDraft,
    string ReleaseNotes,
    Uri ReleasePage,
    IReadOnlyList<GitHubReleaseAssetInfo> Assets)
{
    public bool HasSha256Asset => Assets.Any(asset =>
        (asset.Name.Equals("ReplicaSetup.exe", StringComparison.Ordinal) && asset.Sha256 is not null) ||
        asset.Name.Equals("ReplicaSetup.exe.sha256", StringComparison.OrdinalIgnoreCase) ||
        asset.Name.Equals("SHA256SUMS", StringComparison.OrdinalIgnoreCase));
}

public enum GitHubReleaseCatalogStatus
{
    Success,
    RateLimited,
    NetworkUnavailable,
    TimedOut,
    InvalidResponse,
    ServiceUnavailable,
}

public sealed record GitHubReleaseCatalogResult(
    GitHubReleaseCatalogStatus Status,
    IReadOnlyList<GitHubReleaseDetails> Releases,
    DateTimeOffset? RetryAtUtc = null);

public sealed record UpdatePreference(
    UpdateChannel Channel,
    string? SkippedVersionTag)
{
    public static UpdatePreference Default { get; } = new(UpdateChannel.Stable, null);
}

public enum UpdateDownloadStage
{
    Preparing,
    DownloadingInstaller,
    DownloadingChecksum,
    Verifying,
    Completed,
}

public sealed record UpdateDownloadProgress(
    UpdateDownloadStage Stage,
    long ProcessedBytes,
    long TotalBytes);

public enum UpdateChecksumStatus
{
    Verified,
    Unavailable,
}

public sealed record UpdateDownloadRequest(
    GitHubReleaseDetails Release,
    bool UserApproved);

public sealed record UpdateDownloadOptions(
    long MaximumInstallerSize,
    int MaximumChecksumSize,
    TimeSpan Timeout)
{
    public static UpdateDownloadOptions Default { get; } = new(
        1024L * 1024 * 1024,
        64 * 1024,
        TimeSpan.FromMinutes(10));
}

public sealed record UpdateDownloadResult(
    GitHubReleaseDetails Release,
    string InstallerPath,
    long FileSize,
    string Sha256,
    UpdateChecksumStatus ChecksumStatus,
    DateTimeOffset DownloadedAtUtc);

public enum UpdateDownloadErrorCode
{
    ApprovalRequired,
    MissingAsset,
    InvalidAsset,
    TooLarge,
    TimedOut,
    ChecksumInvalid,
    ChecksumMismatch,
    IoFailure,
}

public sealed class UpdateDownloadException : Exception
{
    public UpdateDownloadException(UpdateDownloadErrorCode code, string message)
        : base(message)
    {
        Code = code;
    }

    public UpdateDownloadException(UpdateDownloadErrorCode code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public UpdateDownloadErrorCode Code { get; }
}

public sealed record UpdateInstallerLaunchRequest(
    UpdateDownloadResult Download,
    bool UserApproved);

public sealed record UpdateInstallerLaunchResult(bool Started, string InstallerPath);
