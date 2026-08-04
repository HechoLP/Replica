using Replica.Core.Snapshots;

namespace Replica.Core.Services;

public interface ISnapshotSelectionEstimator
{
    Task<ReplicaSelectionEstimate> EstimateAsync(
        IReadOnlyList<ReplicaSelectedFolder> selectedFolders,
        IReadOnlyList<ReplicaOfflineInstaller> offlineInstallers,
        CancellationToken cancellationToken);
}
