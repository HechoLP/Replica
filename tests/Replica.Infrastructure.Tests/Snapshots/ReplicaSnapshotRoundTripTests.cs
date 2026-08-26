using System.Buffers.Binary;
using System.Security.Cryptography;
using Replica.Core.Snapshots;
using Replica.Infrastructure.Snapshots;

namespace Replica.Infrastructure.Tests.Snapshots;

public sealed class ReplicaSnapshotRoundTripTests
{
    [Fact]
    public async Task SelectionEstimateReportsPerFileAndTotalSizeBeforeCreation()
    {
        using SnapshotTestContext context = new();
        string selectedDirectory = context.CreateDirectory("estimate");
        await File.WriteAllBytesAsync(Path.Combine(selectedDirectory, "one.bin"), new byte[11]);
        await File.WriteAllBytesAsync(Path.Combine(selectedDirectory, "two.bin"), new byte[17]);
        SnapshotSelectionEstimator estimator = new();

        ReplicaSelectionEstimate estimate = await estimator.EstimateAsync(
            [new ReplicaSelectedFolder(selectedDirectory, "selected/estimate", ReplicaSelectedFolderCategory.Project)],
            [],
            CancellationToken.None);

        Assert.Equal(2, estimate.Files.Count);
        Assert.Equal(28, estimate.TotalSize);
        Assert.All(estimate.Files, file => Assert.StartsWith("files/selected/estimate/", file.ArchivePath));
    }

    [Fact]
    public async Task LightweightSnapshotCreatesRequiredStructureAndReadsBack()
    {
        using SnapshotTestContext context = new();
        string destination = Path.Combine(context.RootPath, "lightweight.replica");
        ReplicaSnapshotWriteRequest request = context.CreateRequest(destination, SnapshotType.Lightweight);

        ReplicaSnapshotManifest manifest = await context.CreateWriter().WriteAsync(
            request,
            progress: null,
            CancellationToken.None);
        ReplicaSnapshotReadResult result = await context.CreateReader().ReadAsync(
            new ReplicaSnapshotReadRequest(destination),
            progress: null,
            CancellationToken.None);

        Assert.Equal(SnapshotType.Lightweight, manifest.SnapshotType);
        Assert.Equal(ReplicaSnapshotManifest.CurrentSchemaVersion, result.Manifest.SchemaVersion);
        Assert.Equal(9, result.EntryPaths.Count);
        Assert.DoesNotContain(result.EntryPaths, path => path.StartsWith("files/", StringComparison.Ordinal));
        Assert.Single(result.Inventory.Applications);
    }

    [Fact]
    public async Task RecoverySnapshotContainsOnlyExplicitlySelectedFiles()
    {
        using SnapshotTestContext context = new();
        string selectedDirectory = context.CreateDirectory("selected");
        await File.WriteAllTextAsync(Path.Combine(selectedDirectory, "notes.txt"), "selected content");
        string unselectedDirectory = context.CreateDirectory("unselected");
        await File.WriteAllTextAsync(Path.Combine(unselectedDirectory, "private.txt"), "not selected");
        string destination = Path.Combine(context.RootPath, "recovery.replica");
        ReplicaSelectedFolder selectedFolder = new(
            selectedDirectory,
            "selected/documents",
            ReplicaSelectedFolderCategory.Documents);
        ReplicaSnapshotWriteRequest request = context.CreateRequest(
            destination,
            SnapshotType.Recovery,
            [selectedFolder]);

        await context.CreateWriter().WriteAsync(request, progress: null, CancellationToken.None);
        ReplicaSnapshotReadResult result = await context.CreateReader().ReadAsync(
            new ReplicaSnapshotReadRequest(destination),
            progress: null,
            CancellationToken.None);

        Assert.Contains("files/selected/documents/notes.txt", result.EntryPaths);
        Assert.DoesNotContain(result.EntryPaths, path => path.Contains("private.txt", StringComparison.Ordinal));
        Assert.Single(result.Manifest.Metadata.Artifacts);
    }

    [Fact]
    public async Task OfflineRecoveryPackContainsExplicitInstallerMetadataAndPayload()
    {
        using SnapshotTestContext context = new();
        string installer = context.CreateFile("inputs/setup.exe", "offline installer bytes");
        byte[] installerBytes = await File.ReadAllBytesAsync(installer);
        string destination = Path.Combine(context.RootPath, "offline.replica");
        ReplicaOfflineInstaller offlineInstaller = new(
            installer,
            "offline/setup.exe",
            "Example Setup",
            "1.0",
            "x64",
            "Vendor download",
            "Review redistribution terms.",
            "CN=Example Publisher",
            new string('A', 64),
            Convert.ToHexString(SHA256.HashData(installerBytes)),
            installerBytes.Length);
        ReplicaSnapshotWriteRequest request = context.CreateRequest(
            destination,
            SnapshotType.OfflineRecoveryPack,
            offlineInstallers: [offlineInstaller]);

        await context.CreateWriter().WriteAsync(request, progress: null, CancellationToken.None);
        ReplicaSnapshotReadResult result = await context.CreateReader().ReadAsync(
            new ReplicaSnapshotReadRequest(destination),
            progress: null,
            CancellationToken.None);

        Assert.Equal(SnapshotType.OfflineRecoveryPack, result.Manifest.SnapshotType);
        Assert.Contains("files/offline/setup.exe", result.EntryPaths);
        Assert.Single(result.Recovery.OfflineInstallers);
    }

    [Fact]
    public async Task OfflineRecoveryPackStopsIfInstallerChangesAfterReview()
    {
        using SnapshotTestContext context = new();
        string installer = context.CreateFile("inputs/reviewed.exe", "reviewed bytes");
        byte[] reviewedBytes = await File.ReadAllBytesAsync(installer);
        ReplicaOfflineInstaller reviewed = new(
            installer,
            "offline/reviewed.exe",
            "Reviewed Setup",
            "1.0",
            "x64",
            "Official vendor download",
            null,
            "CN=Example Publisher",
            new string('A', 64),
            Convert.ToHexString(SHA256.HashData(reviewedBytes)),
            reviewedBytes.Length);
        await File.WriteAllTextAsync(installer, "changed after review");
        string destination = Path.Combine(context.RootPath, "changed.replica");

        ReplicaSnapshotException exception = await Assert.ThrowsAsync<ReplicaSnapshotException>(() =>
            context.CreateWriter().WriteAsync(
                context.CreateRequest(
                    destination,
                    SnapshotType.OfflineRecoveryPack,
                    offlineInstallers: [reviewed]),
                progress: null,
                CancellationToken.None));

        Assert.Contains("changed after publisher", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task HighlyCompressibleSelectedFileStillProducesAReadableSnapshot()
    {
        using SnapshotTestContext context = new();
        string selectedDirectory = context.CreateDirectory("compressible");
        await File.WriteAllBytesAsync(Path.Combine(selectedDirectory, "zeros.bin"), new byte[1024 * 1024]);
        string destination = Path.Combine(context.RootPath, "compressible.replica");
        ReplicaSnapshotWriteRequest request = context.CreateRequest(
            destination,
            SnapshotType.Recovery,
            [new ReplicaSelectedFolder(
                selectedDirectory,
                "selected/compressible",
                ReplicaSelectedFolderCategory.Project)]);

        await context.CreateWriter().WriteAsync(request, progress: null, CancellationToken.None);
        ReplicaSnapshotReadResult result = await context.CreateReader().ReadAsync(
            new ReplicaSnapshotReadRequest(destination),
            progress: null,
            CancellationToken.None);

        Assert.Contains("files/selected/compressible/zeros.bin", result.EntryPaths);
    }

    [Fact]
    public async Task SelectedSensitiveFilesAreExcluded()
    {
        using SnapshotTestContext context = new();
        string selectedDirectory = context.CreateDirectory("project");
        await File.WriteAllTextAsync(Path.Combine(selectedDirectory, ".env"), "TOKEN=secret");
        await File.WriteAllTextAsync(Path.Combine(selectedDirectory, "readme.txt"), "safe");
        string destination = Path.Combine(context.RootPath, "filtered.replica");
        ReplicaSnapshotWriteRequest request = context.CreateRequest(
            destination,
            SnapshotType.Recovery,
            [new ReplicaSelectedFolder(
                selectedDirectory,
                "selected/project",
                ReplicaSelectedFolderCategory.Project)]);

        await context.CreateWriter().WriteAsync(request, progress: null, CancellationToken.None);
        ReplicaSnapshotReadResult result = await context.CreateReader().ReadAsync(
            new ReplicaSnapshotReadRequest(destination),
            progress: null,
            CancellationToken.None);

        Assert.Contains("files/selected/project/readme.txt", result.EntryPaths);
        Assert.DoesNotContain(result.EntryPaths, path => path.EndsWith("/.env", StringComparison.Ordinal));
        Assert.Contains(result.Manifest.Exclusions, exclusion => exclusion.ReasonCode == "SensitiveFile");
    }

    [Fact]
    public async Task ExistingSnapshotIsNeverOverwritten()
    {
        using SnapshotTestContext context = new();
        string destination = context.CreateFile("existing.replica", "original");
        ReplicaSnapshotWriteRequest request = context.CreateRequest(destination, SnapshotType.Lightweight);

        await Assert.ThrowsAsync<IOException>(
            () => context.CreateWriter().WriteAsync(request, progress: null, CancellationToken.None));

        Assert.Equal("original", await File.ReadAllTextAsync(destination));
    }

    [Fact]
    public async Task ReaderRejectsOversizedCentralDirectoryCountBeforeZipArchiveMaterialization()
    {
        using SnapshotTestContext context = new();
        string snapshotPath = Path.Combine(context.RootPath, "too-many-entries.replica");
        byte[] endRecord = new byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(endRecord, 0x06054B50);
        BinaryPrimitives.WriteUInt16LittleEndian(endRecord.AsSpan(8), 10_001);
        BinaryPrimitives.WriteUInt16LittleEndian(endRecord.AsSpan(10), 10_001);
        BinaryPrimitives.WriteUInt32LittleEndian(endRecord.AsSpan(12), 1);
        await File.WriteAllBytesAsync(snapshotPath, endRecord);

        ReplicaSnapshotException exception = await Assert.ThrowsAsync<ReplicaSnapshotException>(() =>
            context.CreateReader().ReadAsync(
                new ReplicaSnapshotReadRequest(snapshotPath),
                progress: null,
                CancellationToken.None));

        Assert.Contains("central directory", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
