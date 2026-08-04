using Replica.Core.Snapshots;

namespace Replica.Core.Services;

public interface ISnapshotWriter
{
    Task<ReplicaSnapshotManifest> WriteAsync(
        ReplicaSnapshotWriteRequest request,
        IProgress<ReplicaSnapshotProgress>? progress,
        CancellationToken cancellationToken);
}
