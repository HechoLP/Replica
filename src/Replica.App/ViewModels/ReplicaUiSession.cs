using Replica.Core.Scanning;

namespace Replica.App.ViewModels;

public sealed class ReplicaUiSession
{
    public EnvironmentScanResult? LatestScan { get; set; }

    public string? LatestSnapshotPath { get; set; }
}
