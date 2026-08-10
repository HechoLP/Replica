namespace Replica.Core.Portable;

public enum PortableStorageKind
{
    GeneralFolder,
    RemovableDrive,
    FixedDrive,
    NetworkDrive,
    CloudSynchronizedFolder,
}

public enum PortableStorageIssueCode
{
    DirectoryMissing,
    ReparsePoint,
    NotWritable,
    StorageUnavailable,
    InsufficientSpace,
    FileTooLargeForFileSystem,
    SynchronizationConflictRisk,
}

public sealed record PortableStorageIssue(
    PortableStorageIssueCode Code,
    string Message,
    bool BlocksExport);

public sealed record PortableStorageInspection(
    string DirectoryPath,
    PortableStorageKind StorageKind,
    string FileSystem,
    long AvailableFreeSpace,
    long? MaximumFileSize,
    bool IsWritable,
    string? CloudProvider,
    IReadOnlyList<PortableStorageIssue> Issues,
    bool IsExternalStorage = false)
{
    public bool CanExport => IsWritable && Issues.All(issue => !issue.BlocksExport);
}

public sealed record PortableSnapshotExportRequest(
    string SourceSnapshotPath,
    string DestinationDirectory,
    string FileName,
    bool TreatAsSynchronizedFolder = false,
    bool ConfirmExternalStorage = false);

public sealed record PortableSnapshotExportResult(
    string SourceSnapshotPath,
    string DestinationPath,
    long FileSize,
    string Sha256,
    PortableStorageInspection Storage,
    DateTimeOffset CompletedAtUtc);

public enum PortableExportStage
{
    Inspecting,
    HashingSource,
    Copying,
    Verifying,
    Completed,
}

public sealed record PortableExportProgress(
    PortableExportStage Stage,
    long ProcessedBytes,
    long TotalBytes);

public sealed record PortableSnapshotSettings(string? DefaultSnapshotDirectory);

public enum PreResetChecklistStatus
{
    Complete,
    Warning,
    ActionRequired,
}

public sealed record PreResetChecklistItem(
    string Id,
    string Title,
    string Guidance,
    PreResetChecklistStatus Status);

public sealed record PreResetChecklistRequest(
    PortableSnapshotExportResult Export,
    bool HasIndependentCopy,
    bool InstallerStoredSeparately,
    bool IsEncrypted,
    bool SnapshotOpenTested,
    IReadOnlyList<string> RequiredAccounts);

public sealed record PreResetChecklist(IReadOnlyList<PreResetChecklistItem> Items)
{
    public bool IsReady => Items.All(item => item.Status != PreResetChecklistStatus.ActionRequired);
}

public sealed class PortableSnapshotException : Exception
{
    public PortableSnapshotException(string message)
        : base(message)
    {
    }

    public PortableSnapshotException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
