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
    string? LicenseWarning);

public sealed record ReplicaFileEstimate(
    string SourcePath,
    string ArchivePath,
    long Size);

public sealed record ReplicaSelectionEstimate(
    IReadOnlyList<ReplicaFileEstimate> Files,
    IReadOnlyList<ReplicaExclusion> Exclusions,
    long TotalSize);
