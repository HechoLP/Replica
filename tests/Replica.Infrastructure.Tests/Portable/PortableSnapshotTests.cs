using System.Security.Cryptography;
using Replica.Core.Portable;
using Replica.Core.Services;
using Replica.Infrastructure.Paths;
using Replica.Infrastructure.Portable;

namespace Replica.Infrastructure.Tests.Portable;

public sealed class PortableSnapshotTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "Replica-Portable-Tests",
        Guid.NewGuid().ToString("N"));

    public PortableSnapshotTests()
    {
        Directory.CreateDirectory(root);
    }

    [Fact]
    public async Task Inspector_AcceptsUsbLikeWritableTemporaryPath()
    {
        WindowsPortableStorageInspector inspector = CreateInspector(
            new StorageVolumeInfo(DriveType.Removable, "exFAT", 10_000, true));

        PortableStorageInspection result = await inspector.InspectAsync(
            root,
            1_000,
            CancellationToken.None);

        Assert.True(result.CanExport);
        Assert.Equal(PortableStorageKind.RemovableDrive, result.StorageKind);
        Assert.Equal("exFAT", result.FileSystem);
        Assert.True(result.IsExternalStorage);
    }

    [Fact]
    public async Task Inspector_BlocksInsufficientSpace()
    {
        WindowsPortableStorageInspector inspector = CreateInspector(
            new StorageVolumeInfo(DriveType.Fixed, "NTFS", 10, true));

        PortableStorageInspection result = await inspector.InspectAsync(
            root,
            11,
            CancellationToken.None);

        Assert.False(result.CanExport);
        Assert.Contains(result.Issues, issue =>
            issue.Code == PortableStorageIssueCode.InsufficientSpace && issue.BlocksExport);
    }

    [Fact]
    public async Task Inspector_BlocksFileLargerThanFat32Limit()
    {
        WindowsPortableStorageInspector inspector = CreateInspector(
            new StorageVolumeInfo(DriveType.Removable, "FAT32", long.MaxValue, true));

        PortableStorageInspection result = await inspector.InspectAsync(
            root,
            (long)uint.MaxValue + 1,
            CancellationToken.None);

        Assert.False(result.CanExport);
        Assert.Equal((long)uint.MaxValue, result.MaximumFileSize);
        Assert.Contains(result.Issues, issue =>
            issue.Code == PortableStorageIssueCode.FileTooLargeForFileSystem);
    }

    [Fact]
    public async Task Inspector_LabelsCloudFolderAndWarnsAboutSynchronization()
    {
        WindowsPortableStorageInspector inspector = CreateInspector(
            new StorageVolumeInfo(DriveType.Fixed, "NTFS", 10_000, true),
            cloudProvider: "Dropbox");

        PortableStorageInspection result = await inspector.InspectAsync(
            root,
            1_000,
            CancellationToken.None);

        Assert.True(result.CanExport);
        Assert.Equal(PortableStorageKind.CloudSynchronizedFolder, result.StorageKind);
        Assert.Equal("Dropbox", result.CloudProvider);
        Assert.Contains(result.Issues, issue =>
            issue.Code == PortableStorageIssueCode.SynchronizationConflictRisk && !issue.BlocksExport);
    }

    [Fact]
    public async Task Exporter_CopiesThroughPartialFileAndVerifiesSha256()
    {
        string source = CreateSnapshot("portable content");
        PortableSnapshotExporter exporter = CreateExporter(new FileHashService());

        PortableSnapshotExportResult result = await exporter.ExportAsync(
            new PortableSnapshotExportRequest(source, root, "usb-copy.replica"),
            null,
            CancellationToken.None);

        Assert.Equal("portable content", await File.ReadAllTextAsync(result.DestinationPath));
        Assert.Equal(await ComputeHashAsync(source), result.Sha256);
        Assert.Empty(Directory.EnumerateFiles(root, "*.partial"));
    }

    [Fact]
    public async Task Exporter_MarksUserSelectedSynchronizationFolder()
    {
        string source = CreateSnapshot("cloud content");
        PortableSnapshotExporter exporter = CreateExporter(new FileHashService());

        PortableSnapshotExportResult result = await exporter.ExportAsync(
            new PortableSnapshotExportRequest(
                source,
                root,
                "cloud-copy.replica",
                TreatAsSynchronizedFolder: true),
            null,
            CancellationToken.None);

        Assert.Equal(PortableStorageKind.CloudSynchronizedFolder, result.Storage.StorageKind);
        Assert.Contains(result.Storage.Issues, issue =>
            issue.Code == PortableStorageIssueCode.SynchronizationConflictRisk);
    }

    [Fact]
    public async Task Exporter_DeletesPartialFileWhenHashVerificationFails()
    {
        string source = CreateSnapshot("portable content");
        PortableSnapshotExporter exporter = CreateExporter(new MismatchingHashService());

        PortableSnapshotException exception = await Assert.ThrowsAsync<PortableSnapshotException>(() =>
            exporter.ExportAsync(
                new PortableSnapshotExportRequest(source, root, "failed.replica"),
                null,
                CancellationToken.None));

        Assert.Contains("SHA-256", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(root, "failed.replica")));
        Assert.Empty(Directory.EnumerateFiles(root, "*.partial"));
    }

    [Fact]
    public async Task Exporter_RefusesExistingFileCollision()
    {
        string source = CreateSnapshot("source");
        string destination = Path.Combine(root, "collision.replica");
        await File.WriteAllTextAsync(destination, "existing");
        PortableSnapshotExporter exporter = CreateExporter(new FileHashService());

        await Assert.ThrowsAsync<PortableSnapshotException>(() => exporter.ExportAsync(
            new PortableSnapshotExportRequest(source, root, "collision.replica"),
            null,
            CancellationToken.None));

        Assert.Equal("existing", await File.ReadAllTextAsync(destination));
    }

    [Fact]
    public async Task Exporter_RejectsReservedWindowsDeviceFileName()
    {
        string source = CreateSnapshot("source");
        PortableSnapshotExporter exporter = CreateExporter(new FileHashService());

        await Assert.ThrowsAsync<PortableSnapshotException>(() => exporter.ExportAsync(
            new PortableSnapshotExportRequest(source, root, "CON.backup.replica"),
            null,
            CancellationToken.None));
    }

    [Fact]
    public async Task Exporter_CancellationRemovesPartialFile()
    {
        string sourceDirectory = Path.Combine(root, "source");
        Directory.CreateDirectory(sourceDirectory);
        string source = Path.Combine(sourceDirectory, "large.replica");
        await File.WriteAllBytesAsync(source, new byte[512 * 1024]);
        PortableSnapshotExporter exporter = CreateExporter(new FileHashService());
        using CancellationTokenSource cancellation = new();
        CancellingProgress progress = new(cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exporter.ExportAsync(
            new PortableSnapshotExportRequest(source, root, "cancelled.replica"),
            progress,
            cancellation.Token));

        Assert.False(File.Exists(Path.Combine(root, "cancelled.replica")));
        Assert.Empty(Directory.EnumerateFiles(root, "*.partial"));
    }

    [Fact]
    public async Task Settings_PersistDefaultSnapshotDirectoryAtomically()
    {
        ReplicaPathProvider paths = new(root);
        Directory.CreateDirectory(paths.SnapshotsDirectory);
        PortableSnapshotSettingsService service = new(paths);

        await service.SaveDefaultDirectoryAsync(paths.SnapshotsDirectory, CancellationToken.None);
        PortableSnapshotSettings result = await service.GetAsync(CancellationToken.None);

        Assert.Equal(Path.GetFullPath(paths.SnapshotsDirectory), result.DefaultSnapshotDirectory);
        Assert.Empty(Directory.EnumerateFiles(paths.ApplicationDataDirectory, "*.tmp"));
    }

    [Fact]
    public void Checklist_RequiresSnapshotOpenTestAndKeepsInstallerSeparate()
    {
        PortableSnapshotExportResult export = new(
            "source.replica",
            "usb.replica",
            100,
            new string('A', 64),
            new PortableStorageInspection(
                root,
                PortableStorageKind.RemovableDrive,
                "exFAT",
                1000,
                null,
                true,
                null,
                []),
            DateTimeOffset.UtcNow);
        PreResetChecklistService service = new();

        PreResetChecklist result = service.Create(new PreResetChecklistRequest(
            export,
            HasIndependentCopy: true,
            InstallerStoredSeparately: true,
            IsEncrypted: true,
            SnapshotOpenTested: false,
            RequiredAccounts: ["Microsoft"]));

        Assert.False(result.IsReady);
        Assert.Equal(
            PreResetChecklistStatus.ActionRequired,
            result.Items.Single(item => item.Id == "open-test").Status);
        Assert.Equal(
            PreResetChecklistStatus.Complete,
            result.Items.Single(item => item.Id == "installer").Status);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private WindowsPortableStorageInspector CreateInspector(
        StorageVolumeInfo volume,
        bool canWrite = true,
        string? cloudProvider = null) => new(
            new FakeVolumeProbe(volume),
            new FakeWriteProbe(canWrite),
            new FakeCloudFolderLocator(cloudProvider));

    private PortableSnapshotExporter CreateExporter(IFileHashService hashes) => new(
        new FakeStorageInspector(root),
        hashes,
        TimeProvider.System);

    private string CreateSnapshot(string content)
    {
        string sourceDirectory = Path.Combine(root, "source");
        Directory.CreateDirectory(sourceDirectory);
        string path = Path.Combine(sourceDirectory, $"{Guid.NewGuid():N}.replica");
        File.WriteAllText(path, content);
        return path;
    }

    private static async Task<string> ComputeHashAsync(string path)
    {
        await using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private sealed class FakeVolumeProbe(StorageVolumeInfo volume) : IStorageVolumeProbe
    {
        public StorageVolumeInfo Inspect(string directoryPath) => volume;
    }

    private sealed class FakeWriteProbe(bool result) : IPortableWriteProbe
    {
        public Task<bool> CanWriteAsync(string directoryPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
    }

    private sealed class FakeCloudFolderLocator(string? provider) : ICloudFolderLocator
    {
        public string? FindProvider(string directoryPath) => provider;
    }

    private sealed class FakeStorageInspector(string directory) : IPortableStorageInspector
    {
        public Task<PortableStorageInspection> InspectAsync(
            string directoryPath,
            long requiredBytes,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new PortableStorageInspection(
                directory,
                PortableStorageKind.RemovableDrive,
                "exFAT",
                long.MaxValue,
                null,
                true,
                null,
                []));
        }
    }

    private sealed class MismatchingHashService : IFileHashService
    {
        private int callCount;

        public Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            callCount++;
            return Task.FromResult(new string(callCount == 1 ? 'A' : 'B', 64));
        }
    }

    private sealed class CancellingProgress(CancellationTokenSource cancellation) :
        IProgress<PortableExportProgress>
    {
        public void Report(PortableExportProgress value)
        {
            if (value.Stage == PortableExportStage.Copying && value.ProcessedBytes > 0)
            {
                cancellation.Cancel();
            }
        }
    }
}
