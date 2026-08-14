using Replica.Core.Diffing;
using Replica.Core.Scanning;

namespace Replica.App.ViewModels;

public sealed class ReplicaUiSession
{
    public EnvironmentScanResult? LatestScan { get; set; }

    public string? LatestSnapshotPath { get; set; }

    public DiffEnvironmentState? LatestSnapshotEnvironment { get; set; }

    public DiffEnvironmentState? LatestCurrentEnvironment { get; set; }

    public EnvironmentDiffResult? LatestDiff { get; set; }

    public void ClearComparison()
    {
        LatestSnapshotPath = null;
        LatestSnapshotEnvironment = null;
        LatestCurrentEnvironment = null;
        LatestDiff = null;
    }
}
