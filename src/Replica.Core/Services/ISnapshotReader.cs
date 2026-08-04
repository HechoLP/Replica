using Replica.Core.Snapshots;

namespace Replica.Core.Services;

public interface ISnapshotReader
{
    Task<ReplicaSnapshotReadResult> ReadAsync(
        ReplicaSnapshotReadRequest request,
        IProgress<ReplicaSnapshotProgress>? progress,
        CancellationToken cancellationToken);
}
