namespace Replica.Core.Snapshots;

public sealed record ReplicaExclusion(
    string Path,
    string ReasonCode,
    string? Detail = null);

public sealed record ReplicaRecoveryOptions(
    string ConflictBehavior,
    int RestorePriority,
    string? CleanInstallNotes,
    IReadOnlyList<ReplicaSelectedFolder> SelectedFolders,
    IReadOnlyList<ReplicaOfflineInstaller> OfflineInstallers);

public sealed record ReplicaSelectedFolder(
    string SourcePath,
    string ArchivePath,
    ReplicaSelectedFolderCategory Category);

public enum ReplicaSelectedFolderCategory
{
    Desktop,
    Documents,
    Project,
    GameSave,
    ApplicationSettings,
}

public sealed record ReplicaOfflineInstaller(
    string SourcePath,
    string ArchivePath,
    string DisplayName,
    string? Version,
    string? Architecture,
    string Provenance,
    string? LicenseWarning,
    string? Publisher = null,
    string? PublisherCertificateSha256 = null,
    string? ExpectedSha256 = null,
    long? ExpectedSize = null);

public enum OfflineInstallerSignatureStatus
{
    Trusted,
    Unsigned,
    Untrusted,
    VerificationUnavailable,
}

public sealed record OfflineInstallerInspection(
    string SourcePath,
    string DisplayName,
    string? Version,
    string Architecture,
    long FileSize,
    string Sha256,
    OfflineInstallerSignatureStatus SignatureStatus,
    string? Publisher,
    string? PublisherCertificateSha256,
    string StatusMessage)
{
    public bool CanInclude => SignatureStatus == OfflineInstallerSignatureStatus.Trusted;
}

public sealed record ExportedOfflineInstaller(
    string DisplayName,
    string DestinationPath,
    long FileSize,
    string Sha256,
    string Publisher);

public sealed record OfflineInstallerExportResult(
    string DestinationDirectory,
    IReadOnlyList<ExportedOfflineInstaller> Installers)
{
    public long TotalBytes => Installers.Sum(installer => installer.FileSize);
}

public sealed record ReplicaFileEstimate(
    string SourcePath,
    string ArchivePath,
    long Size);

public sealed record ReplicaSelectionEstimate(
    IReadOnlyList<ReplicaFileEstimate> Files,
    IReadOnlyList<ReplicaExclusion> Exclusions,
    long TotalSize);
