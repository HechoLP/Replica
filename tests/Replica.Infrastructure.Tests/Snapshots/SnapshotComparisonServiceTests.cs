using Replica.Core.Diffing;
using Replica.Core.Scanning;
using Replica.Core.Services;
using Replica.Core.Snapshots;
using Replica.Infrastructure.Snapshots;

namespace Replica.Infrastructure.Tests.Snapshots;

public sealed class SnapshotComparisonServiceTests
{
    [Fact]
    public async Task CompareAsync_MapsSnapshotAndCurrentPluginInventoryWithoutExposingFileContents()
    {
        CapturingDiffEngine diffEngine = new();
        SnapshotComparisonService service = new(
            new FakeSnapshotReader(CreateSnapshot(ReplicaPlatformFamily.Windows)),
            new FakeEnvironmentScanner(),
            diffEngine);

        SnapshotEnvironmentComparisonResult result = await service.CompareAsync(
            "C:\\Fixture\\snapshot.replica",
            ReadOnlyMemory<char>.Empty,
            DiffRestoreMode.Safe,
            CancellationToken.None);

        Assert.Single(result.SnapshotEnvironment.Applications);
        Assert.Contains(result.SnapshotEnvironment.Values, value =>
            value.Area == DiffArea.EnvironmentVariables && value.Key == "User:EDITOR");
        DiffValueEntry pluginFile = Assert.Single(result.SnapshotEnvironment.Values, value =>
            value.Area == DiffArea.ConfigurationFiles);
        Assert.Null(pluginFile.Value);
        Assert.NotNull(pluginFile.Hash);
        Assert.DoesNotContain("secret-free-content", pluginFile.Hash, StringComparison.Ordinal);
        Assert.Same(result.SnapshotEnvironment, diffEngine.Source);
        Assert.Same(result.CurrentEnvironmentState, diffEngine.Target);
    }

    [Fact]
    public async Task CompareAsync_RejectsMacSnapshotBeforeScanningWindows()
    {
        FakeEnvironmentScanner scanner = new();
        SnapshotComparisonService service = new(
            new FakeSnapshotReader(CreateSnapshot(ReplicaPlatformFamily.MacOS)),
            scanner,
            new CapturingDiffEngine());

        await Assert.ThrowsAsync<ReplicaSnapshotException>(() => service.CompareAsync(
            "C:\\Fixture\\snapshot.replica",
            ReadOnlyMemory<char>.Empty,
            DiffRestoreMode.Safe,
            CancellationToken.None));
        Assert.Equal(0, scanner.CallCount);
    }

    [Fact]
    public async Task CompareAsync_BlocksPlanningWhenCurrentInventoryIsIncomplete()
    {
        EnvironmentScanResult incomplete = CreateCurrentScan() with
        {
            IncompleteStages = [EnvironmentScanStage.Applications],
        };
        SnapshotComparisonService service = new(
            new FakeSnapshotReader(CreateSnapshot(ReplicaPlatformFamily.Windows)),
            new FakeEnvironmentScanner(incomplete),
            new CapturingDiffEngine());

        ReplicaSnapshotException exception = await Assert.ThrowsAsync<ReplicaSnapshotException>(() =>
            service.CompareAsync(
                "C:\\Fixture\\snapshot.replica",
                ReadOnlyMemory<char>.Empty,
                DiffRestoreMode.Safe,
                CancellationToken.None));

        Assert.Contains("incomplete", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompareAsync_DefensivelyRedactsSensitiveSnapshotEnvironmentValues()
    {
        ReplicaSnapshotReadResult snapshot = CreateSnapshot(ReplicaPlatformFamily.Windows);
        snapshot = snapshot with
        {
            Inventory = snapshot.Inventory with
            {
                Environment = snapshot.Inventory.Environment with
                {
                    Variables = [new ReplicaEnvironmentVariable("SIGNING_KEY", "never-display", "User")],
                },
            },
        };
        SnapshotComparisonService service = new(
            new FakeSnapshotReader(snapshot),
            new FakeEnvironmentScanner(),
            new CapturingDiffEngine());

        SnapshotEnvironmentComparisonResult result = await service.CompareAsync(
            "C:\\Fixture\\snapshot.replica",
            ReadOnlyMemory<char>.Empty,
            DiffRestoreMode.Safe,
            CancellationToken.None);

        DiffValueEntry value = Assert.Single(result.SnapshotEnvironment.Values, candidate =>
            candidate.Key == "User:SIGNING_KEY");
        Assert.True(value.IsSensitiveExcluded);
        Assert.False(value.CanRestoreAutomatically);
        Assert.Null(value.Value);
    }

    private static ReplicaSnapshotReadResult CreateSnapshot(ReplicaPlatformFamily platform)
    {
        ReplicaWindowsInfo windows = new(
            "Windows 11 Pro",
            "11",
            "26100",
            "X64",
            "en-US",
            "UTC",
            []);
        ReplicaMachineInfo machine = new("SOURCE", windows, "X64", "en-US");
        ReplicaSnapshotManifest manifest = new(
            ReplicaSnapshotManifest.CurrentSchemaVersion,
            "0.2.0",
            Guid.NewGuid(),
            SnapshotType.Lightweight,
            DateTimeOffset.UtcNow,
            machine.MachineName,
            windows.Version,
            machine.Architecture,
            machine.Locale,
            [],
            [],
            new ReplicaSnapshotMetadata(machine, []),
            SourcePlatform: platform);
        ReplicaSnapshotInventory inventory = new(
            [new ReplicaApplication(
                "Git",
                "2.50.0",
                "Git Project",
                "X64",
                "User",
                "WinGet",
                new ReplicaPackageIdentity("Git.Git", null, null),
                [])],
            new ReplicaEnvironmentInventory(
                [new ReplicaEnvironmentVariable("EDITOR", "code", "User")],
                []),
            windows,
            [],
            [new ReplicaPluginSnapshot(
                "replica.test",
                "1.0.0",
                [],
                [],
                new Dictionary<string, string>(StringComparer.Ordinal) { ["theme"] = "dark" },
                [new ReplicaPluginFile("settings.json", "secret-free-content", "application/json")])]);
        return new ReplicaSnapshotReadResult(
            manifest,
            inventory,
            new ReplicaRecoveryOptions("RenameAndKeepBoth", 0, null, [], []),
            [],
            []);
    }

    private sealed class FakeSnapshotReader(ReplicaSnapshotReadResult result) : ISnapshotReader
    {
        public Task<ReplicaSnapshotReadResult> ReadAsync(
            ReplicaSnapshotReadRequest request,
            IProgress<ReplicaSnapshotProgress>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
    }

    private static EnvironmentScanResult CreateCurrentScan() => new(
        null,
        [],
        [],
        [],
        [],
        [],
        [],
        new EnvironmentScanSummary(0, 0, 0, 0, 0, 0));

    private sealed class FakeEnvironmentScanner(EnvironmentScanResult? result = null) : IEnvironmentScanner
    {
        public int CallCount { get; private set; }

        public Task<EnvironmentScanResult> ScanAsync(
            IProgress<EnvironmentScanProgress>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(result ?? CreateCurrentScan());
        }
    }

    private sealed class CapturingDiffEngine : IEnvironmentDiffEngine
    {
        public DiffEnvironmentState? Source { get; private set; }

        public DiffEnvironmentState? Target { get; private set; }

        public EnvironmentDiffResult Compare(
            DiffEnvironmentState source,
            DiffEnvironmentState target,
            DiffRestoreMode mode,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Source = source;
            Target = target;
            return new EnvironmentDiffResult(
                mode,
                [],
                new EnvironmentSimilarityScore(null, 0, 0, 0, 0, []));
        }
    }
}
