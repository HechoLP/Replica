using Replica.Core.Diffing;
using Replica.Core.Scanning;
using Replica.Core.Snapshots;

namespace Replica.Core.Services;

public interface ISnapshotComparisonService
{
    Task<SnapshotEnvironmentComparisonResult> CompareAsync(
        string snapshotPath,
        ReadOnlyMemory<char> password,
        DiffRestoreMode mode,
        CancellationToken cancellationToken);
}

public sealed record SnapshotEnvironmentComparisonResult(
    ReplicaSnapshotManifest Manifest,
    EnvironmentScanResult CurrentEnvironment,
    DiffEnvironmentState SnapshotEnvironment,
    DiffEnvironmentState CurrentEnvironmentState,
    EnvironmentDiffResult Diff);
