using Replica.Core.Services;
using Replica.Core.Snapshots;
using Replica.Infrastructure.Snapshots;

namespace Replica.Infrastructure.Tests.Snapshots;

internal sealed class SnapshotTestContext : IDisposable
{
    public SnapshotTestContext()
    {
        RootPath = Path.Combine(Path.GetTempPath(), "Replica.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootPath);
    }

    public string RootPath { get; }

    public ReplicaSnapshotReader CreateReader(ReplicaSnapshotReadLimits? limits = null)
    {
        return limits is null ? new ReplicaSnapshotReader() : new ReplicaSnapshotReader(limits);
    }

    public ReplicaSnapshotWriter CreateWriter(
        ISnapshotReader? reader = null,
        ISnapshotSelectionEstimator? estimator = null)
    {
        return new ReplicaSnapshotWriter(
            estimator ?? new SnapshotSelectionEstimator(),
            reader ?? CreateReader());
    }

    public ReplicaSnapshotWriteRequest CreateRequest(
        string destinationPath,
        SnapshotType snapshotType,
        IReadOnlyList<ReplicaSelectedFolder>? selectedFolders = null,
        IReadOnlyList<ReplicaOfflineInstaller>? offlineInstallers = null,
        ReplicaSnapshotEncryptionOptions? encryption = null)
    {
        ReplicaWindowsInfo windows = new(
            "Windows 11 Pro",
            "10.0.26100",
            "26100",
            "x64",
            "ko-KR",
            "Korea Standard Time",
            ["Inventory"]);
        ReplicaMachineInfo machine = new("TEST-MACHINE", windows, "x64", "ko-KR");
        ReplicaSnapshotInventory inventory = new(
            [
                new ReplicaApplication(
                    "Example App",
                    "1.0.0",
                    "Example Publisher",
                    "x64",
                    "User",
                    "Winget",
                    new ReplicaPackageIdentity("Example.App", null, null),
                    []),
            ],
            new ReplicaEnvironmentInventory(
                [new ReplicaEnvironmentVariable("REPLICA_TEST", "enabled", "User")],
                [new ReplicaPathEntry("C:\\Tools", "User", 0)]),
            windows,
            [new ReplicaFontInfo("Example Sans", "Regular", "1.0", "ExampleSans-Regular")],
            [new ReplicaPluginSnapshot("built-in.example", "1.0", ["Settings"], [])]);
        ReplicaRecoveryOptions recovery = new(
            "KeepCurrent",
            0,
            "Test recovery metadata",
            selectedFolders ?? [],
            offlineInstallers ?? []);

        return new ReplicaSnapshotWriteRequest(
            destinationPath,
            snapshotType,
            "0.1.0-alpha.1",
            machine,
            inventory,
            ["Inventory", "Checksums"],
            [],
            recovery,
            encryption);
    }

    public string CreateDirectory(string name)
    {
        string path = Path.Combine(RootPath, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public string CreateFile(string relativePath, string content)
    {
        string path = Path.Combine(RootPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(RootPath))
        {
            Directory.Delete(RootPath, recursive: true);
        }
    }
}
