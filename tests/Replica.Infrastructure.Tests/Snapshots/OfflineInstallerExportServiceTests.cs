using System.Security.Cryptography;
using Replica.Core.Snapshots;
using Replica.Infrastructure.Snapshots;

namespace Replica.Infrastructure.Tests.Snapshots;

public sealed class OfflineInstallerExportServiceTests
{
    [Fact]
    public async Task ExportAsync_RevalidatesEncryptedPackAndUsesNonConflictingName()
    {
        using SnapshotTestContext context = new();
        string installerPath = context.CreateFile("input/Setup-x64.exe", "trusted installer payload");
        byte[] installerBytes = await File.ReadAllBytesAsync(installerPath);
        string snapshotPath = Path.Combine(context.RootPath, "offline-encrypted.replica");
        char[] password = "correct horse battery staple".ToCharArray();
        ReplicaOfflineInstaller installer = CreateInstaller(installerPath, installerBytes);
        ReplicaSnapshotManifest manifest;
        try
        {
            manifest = await context.CreateWriter().WriteAsync(
                context.CreateRequest(
                    snapshotPath,
                    SnapshotType.OfflineRecoveryPack,
                    offlineInstallers: [installer],
                    encryption: new ReplicaSnapshotEncryptionOptions(password)),
                progress: null,
                CancellationToken.None);
        }
        finally
        {
            Array.Clear(password);
        }

        string destination = context.CreateDirectory("export");
        string existing = context.CreateFile("export/Setup-x64.exe", "existing file");
        string snapshotHash = Convert.ToHexString(
            SHA256.HashData(await File.ReadAllBytesAsync(snapshotPath)));
        OfflineInstallerExportService service = new(
            context.CreateReader(),
            new FakeInspectionService(trusted: true));
        char[] exportPassword = "correct horse battery staple".ToCharArray();

        OfflineInstallerExportResult result;
        try
        {
            result = await service.ExportAsync(
                snapshotPath,
                manifest.SnapshotId,
                snapshotHash,
                destination,
                exportPassword,
                CancellationToken.None);
        }
        finally
        {
            Array.Clear(exportPassword);
        }

        ExportedOfflineInstaller exported = Assert.Single(result.Installers);
        Assert.Equal("existing file", await File.ReadAllTextAsync(existing));
        Assert.EndsWith("Setup-x64 (2).exe", exported.DestinationPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(installerBytes, await File.ReadAllBytesAsync(exported.DestinationPath));
        Assert.DoesNotContain(Directory.EnumerateFiles(destination), path =>
            Path.GetFileName(path).StartsWith(".replica-export-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExportAsync_RejectsChangedPackDigestBeforeWritingFiles()
    {
        using SnapshotTestContext context = new();
        (string snapshotPath, ReplicaSnapshotManifest manifest) = await CreatePackAsync(context);
        string destination = context.CreateDirectory("digest-export");
        OfflineInstallerExportService service = new(
            context.CreateReader(),
            new FakeInspectionService(trusted: true));

        await Assert.ThrowsAsync<ReplicaSnapshotException>(() => service.ExportAsync(
            snapshotPath,
            manifest.SnapshotId,
            new string('0', 64),
            destination,
            ReadOnlyMemory<char>.Empty,
            CancellationToken.None));

        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
    }

    [Fact]
    public async Task ExportAsync_RemovesPartialOutputWhenPublisherTrustFails()
    {
        using SnapshotTestContext context = new();
        (string snapshotPath, ReplicaSnapshotManifest manifest) = await CreatePackAsync(context);
        string destination = context.CreateDirectory("untrusted-export");
        string snapshotHash = Convert.ToHexString(
            SHA256.HashData(await File.ReadAllBytesAsync(snapshotPath)));
        OfflineInstallerExportService service = new(
            context.CreateReader(),
            new FakeInspectionService(trusted: false));

        await Assert.ThrowsAsync<ReplicaSnapshotException>(() => service.ExportAsync(
            snapshotPath,
            manifest.SnapshotId,
            snapshotHash,
            destination,
            ReadOnlyMemory<char>.Empty,
            CancellationToken.None));

        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
    }

    [Fact]
    public async Task ExportAsync_RejectsTemporaryPathReplacementBeforePublication()
    {
        using SnapshotTestContext context = new();
        (string snapshotPath, ReplicaSnapshotManifest manifest) = await CreatePackAsync(context);
        string destination = context.CreateDirectory("race-export");
        string snapshotHash = Convert.ToHexString(
            SHA256.HashData(await File.ReadAllBytesAsync(snapshotPath)));
        OfflineInstallerExportService service = new(
            context.CreateReader(),
            new ReplacingInspectionService());

        await Assert.ThrowsAsync<ReplicaSnapshotException>(() => service.ExportAsync(
            snapshotPath,
            manifest.SnapshotId,
            snapshotHash,
            destination,
            ReadOnlyMemory<char>.Empty,
            CancellationToken.None));

        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
    }

    private static async Task<(string Path, ReplicaSnapshotManifest Manifest)> CreatePackAsync(
        SnapshotTestContext context)
    {
        string installerPath = context.CreateFile("input/setup.exe", "trusted installer payload");
        byte[] installerBytes = await File.ReadAllBytesAsync(installerPath);
        string snapshotPath = Path.Combine(context.RootPath, $"offline-{Guid.NewGuid():N}.replica");
        ReplicaSnapshotManifest manifest = await context.CreateWriter().WriteAsync(
            context.CreateRequest(
                snapshotPath,
                SnapshotType.OfflineRecoveryPack,
                offlineInstallers: [CreateInstaller(installerPath, installerBytes)]),
            progress: null,
            CancellationToken.None);
        return (snapshotPath, manifest);
    }

    private static ReplicaOfflineInstaller CreateInstaller(string path, byte[] content) => new(
        path,
        "offline/setup.exe",
        "Example Setup",
        "1.0",
        "x64",
        "https://vendor.example/download",
        "Review vendor license terms.",
        "CN=Trusted Vendor",
        new string('A', 64),
        Convert.ToHexString(SHA256.HashData(content)),
        content.Length);

    private sealed class FakeInspectionService(bool trusted) : Replica.Core.Services.IOfflineInstallerInspectionService
    {
        public async Task<OfflineInstallerInspection> InspectAsync(
            string installerPath,
            CancellationToken cancellationToken)
        {
            byte[] content = await File.ReadAllBytesAsync(installerPath, cancellationToken);
            return new OfflineInstallerInspection(
                installerPath,
                "Example Setup",
                "1.0",
                "x64",
                content.Length,
                Convert.ToHexString(SHA256.HashData(content)),
                trusted
                    ? OfflineInstallerSignatureStatus.Trusted
                    : OfflineInstallerSignatureStatus.Untrusted,
                "CN=Trusted Vendor",
                new string('A', 64),
                trusted ? "Trusted" : "Untrusted");
        }
    }

    private sealed class ReplacingInspectionService : Replica.Core.Services.IOfflineInstallerInspectionService
    {
        private int inspections;

        public async Task<OfflineInstallerInspection> InspectAsync(
            string installerPath,
            CancellationToken cancellationToken)
        {
            byte[] content = await File.ReadAllBytesAsync(installerPath, cancellationToken);
            if (Interlocked.Increment(ref inspections) == 1)
            {
                File.Delete(installerPath);
                await File.WriteAllBytesAsync(
                    installerPath,
                    "attacker replacement!!"u8.ToArray(),
                    cancellationToken);
            }

            return new OfflineInstallerInspection(
                installerPath,
                "Example Setup",
                "1.0",
                "x64",
                content.Length,
                Convert.ToHexString(SHA256.HashData(content)),
                OfflineInstallerSignatureStatus.Trusted,
                "CN=Trusted Vendor",
                new string('A', 64),
                "Trusted");
        }
    }
}
