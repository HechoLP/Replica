using Replica.Core.History;
using Replica.Core.Planning;
using Replica.Core.Recovery;

namespace Replica.Core.Services;

public interface ISnapshotHistoryService
{
    Task InitializeAsync(CancellationToken cancellationToken);

    Task<SnapshotHistoryEntry> AddSnapshotAsync(
        string snapshotPath,
        ReadOnlyMemory<char> password,
        int? environmentScore,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SnapshotHistoryEntry>> GetSnapshotsAsync(
        CancellationToken cancellationToken);

    Task UpdateSnapshotAsync(
        Guid snapshotId,
        SnapshotHistoryUpdate update,
        CancellationToken cancellationToken);

    Task DeleteSnapshotAsync(
        Guid snapshotId,
        bool deleteSnapshotFile,
        bool userConfirmed,
        CancellationToken cancellationToken);

    Task<SnapshotComparisonResult> CompareAsync(
        Guid fromSnapshotId,
        Guid toSnapshotId,
        CancellationToken cancellationToken);

    Task<PastStateRestorePlan> CreatePastStateRestorePlanAsync(
        Guid snapshotId,
        IReadOnlyList<RecoveryPathMapping> mappings,
        ReadOnlyMemory<char> password,
        CancellationToken cancellationToken);

    Task RecordRestoreAsync(RestoreHistoryRecord record, CancellationToken cancellationToken);

    Task RecordRollbackAsync(RollbackHistoryRecord record, CancellationToken cancellationToken);

    Task SavePackageMatchingOverrideAsync(
        PackageMatchingOverride matchingOverride,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PackageMatchingOverride>> GetPackageMatchingOverridesAsync(
        CancellationToken cancellationToken);
}
