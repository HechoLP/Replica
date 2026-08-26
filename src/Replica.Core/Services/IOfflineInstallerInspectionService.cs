using Replica.Core.Snapshots;

namespace Replica.Core.Services;

public interface IOfflineInstallerInspectionService
{
    Task<OfflineInstallerInspection> InspectAsync(
        string installerPath,
        CancellationToken cancellationToken);
}

public interface IOfflineInstallerExportService
{
    Task<OfflineInstallerExportResult> ExportAsync(
        string snapshotPath,
        Guid expectedSnapshotId,
        string expectedSnapshotSha256,
        string destinationDirectory,
        ReadOnlyMemory<char> password,
        CancellationToken cancellationToken);
}
