using Replica.Core.Portable;

namespace Replica.Core.Services;

public interface IPortableStorageInspector
{
    Task<PortableStorageInspection> InspectAsync(
        string directoryPath,
        long requiredBytes,
        CancellationToken cancellationToken);
}

public interface IFileHashService
{
    Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken);
}

public interface IPortableSnapshotExporter
{
    Task<PortableSnapshotExportResult> ExportAsync(
        PortableSnapshotExportRequest request,
        IProgress<PortableExportProgress>? progress,
        CancellationToken cancellationToken);
}

public interface IPortableSnapshotSettingsService
{
    Task<PortableSnapshotSettings> GetAsync(CancellationToken cancellationToken);

    Task SaveDefaultDirectoryAsync(string directoryPath, CancellationToken cancellationToken);
}

public interface IPreResetChecklistService
{
    PreResetChecklist Create(PreResetChecklistRequest request);
}

public interface IOfficialReleaseSource
{
    Task<IReadOnlyList<OfficialReplicaRelease>> GetReleasesAsync(CancellationToken cancellationToken);

    Task<Stream> OpenAssetStreamAsync(
        OfficialReleaseAsset asset,
        CancellationToken cancellationToken);
}

public interface IReplicaInstallerDownloadService
{
    Task<ReplicaInstallerDownloadResult> DownloadAsync(
        ReplicaInstallerDownloadRequest request,
        IProgress<ReplicaInstallerDownloadProgress>? progress,
        CancellationToken cancellationToken);
}
