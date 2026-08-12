using Replica.Core.Platforms;
using Replica.Core.Services;
using Replica.Core.Snapshots;
using Replica.Infrastructure.Snapshots;
using Replica.Mac.Infrastructure.Snapshots;

namespace Replica.Mac.Tests;

public sealed class MacSnapshotTests
{
    [Fact]
    public async Task LightweightSnapshotRoundTripsWithMacPlatformAndRedactions()
    {
        using TemporaryDirectory temporary = new();
        string destination = Path.Combine(temporary.Path, "mac.replica");
        ISnapshotReader reader = new ReplicaSnapshotReader();
        ISnapshotWriter writer = new ReplicaSnapshotWriter(new EmptySnapshotSelectionEstimator(), reader);
        MacLightweightSnapshotService service = new(writer);
        PlatformScanResult scan = CreateScan();

        ReplicaSnapshotManifest manifest = await service.CreateAsync(
            destination,
            "0.2.0-alpha.1",
            scan,
            progress: null,
            CancellationToken.None);
        ReplicaSnapshotReadResult read = await reader.ReadAsync(
            new ReplicaSnapshotReadRequest(destination),
            progress: null,
            CancellationToken.None);

        Assert.Equal(ReplicaPlatformFamily.MacOS, manifest.SourcePlatform);
        Assert.Equal(ReplicaPlatformFamily.MacOS, read.Manifest.SourcePlatform);
        Assert.Equal("com.example.editor", Assert.Single(read.Inventory.Applications).PackageIdentity.MacBundleIdentifier);
        Assert.DoesNotContain(read.Inventory.Environment.Variables, variable => variable.Name == "API_TOKEN");
        Assert.Contains(read.Manifest.Exclusions, exclusion => exclusion.Path == "API_TOKEN");
    }

    private static PlatformScanResult CreateScan()
    {
        ReplicaPlatformInfo platform = new(
            ReplicaPlatformFamily.MacOS,
            "macOS",
            "15.1",
            "24B1",
            "Arm64",
            "ko-KR",
            "Asia/Seoul",
            ["MacOS", "SnapshotV1"]);
        PlatformApplication application = new(
            "Editor",
            "1.0",
            "example",
            "/Applications/Editor.app",
            "com.example.editor",
            null,
            "ApplicationBundle",
            "Arm64",
            false);
        IReadOnlyList<PlatformEnvironmentVariable> variables =
        [
            new("LANG", "ko_KR.UTF-8", false, null),
            new("API_TOKEN", null, true, "SensitiveName"),
        ];
        return new PlatformScanResult(
            platform,
            "TestMac",
            [application],
            variables,
            [new PlatformPathEntry("/usr/bin", 0, false, true)],
            [],
            [],
            new PlatformScanSummary(1, 0, 2, 1, 0, 0));
    }
}
