namespace Replica.Core.Portable;

public sealed record OfficialReleaseAsset(
    string Name,
    Uri DownloadUri,
    long Size,
    string? Sha256);

public sealed record OfficialReplicaRelease(
    Version Version,
    string TagName,
    Uri ReleasePage,
    bool IsPrerelease,
    bool IsDraft,
    DateTimeOffset PublishedAtUtc,
    IReadOnlyList<OfficialReleaseAsset> Assets);

public enum ReplicaReleaseSelection
{
    LatestStable,
    SpecificVersion,
}

public sealed record ReplicaInstallerDownloadRequest(
    string DestinationDirectory,
    ReplicaReleaseSelection Selection,
    string? VersionTag,
    bool UserApproved);

public enum ReplicaInstallerDownloadStage
{
    ResolvingRelease,
    Downloading,
    Verifying,
    Completed,
}

public sealed record ReplicaInstallerDownloadProgress(
    ReplicaInstallerDownloadStage Stage,
    long ProcessedBytes,
    long TotalBytes);

public sealed record ReplicaInstallerDownloadResult(
    string InstallerPath,
    string VersionTag,
    long FileSize,
    string Sha256,
    bool HashWasProvidedByRelease,
    Uri ReleasePage);

public sealed class ReplicaInstallerDownloadException : Exception
{
    public ReplicaInstallerDownloadException(string message)
        : base(message)
    {
    }

    public ReplicaInstallerDownloadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
