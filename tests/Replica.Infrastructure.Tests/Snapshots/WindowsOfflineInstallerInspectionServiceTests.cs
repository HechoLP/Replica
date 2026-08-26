using System.Security.Cryptography;
using Replica.Core.Snapshots;
using Replica.Infrastructure.Security;
using Replica.Infrastructure.Snapshots;

namespace Replica.Infrastructure.Tests.Snapshots;

public sealed class WindowsOfflineInstallerInspectionServiceTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "Replica-Offline-Installer-Tests",
        Guid.NewGuid().ToString("N"));

    public WindowsOfflineInstallerInspectionServiceTests()
    {
        Directory.CreateDirectory(root);
    }

    [Fact]
    public async Task InspectAsync_BindsTrustedPublisherHashSizeAndArchitecture()
    {
        string path = CreatePortableExecutable("VendorSetup-x64.exe", machine: 0x8664);
        byte[] bytes = await File.ReadAllBytesAsync(path);
        WindowsOfflineInstallerInspectionService service = new(new FakeAuthenticodeTrustService(
            OfflineInstallerSignatureStatus.Trusted,
            "CN=Trusted Vendor",
            new string('A', 64)));

        OfflineInstallerInspection result = await service.InspectAsync(path, CancellationToken.None);

        Assert.True(result.CanInclude);
        Assert.Equal("x64", result.Architecture);
        Assert.Equal(bytes.Length, result.FileSize);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), result.Sha256);
        Assert.Equal("CN=Trusted Vendor", result.Publisher);
        Assert.Equal(new string('A', 64), result.PublisherCertificateSha256);
    }

    [Fact]
    public async Task InspectAsync_ReportsUntrustedSignatureWithoutApprovingInclusion()
    {
        string path = CreatePortableExecutable("Untrusted.exe", machine: 0x014c);
        WindowsOfflineInstallerInspectionService service = new(new FakeAuthenticodeTrustService(
            OfflineInstallerSignatureStatus.Untrusted,
            "CN=Unknown Vendor",
            new string('B', 64)));

        OfflineInstallerInspection result = await service.InspectAsync(path, CancellationToken.None);

        Assert.False(result.CanInclude);
        Assert.Equal(OfflineInstallerSignatureStatus.Untrusted, result.SignatureStatus);
    }

    [Fact]
    public async Task InspectAsync_RejectsUnsupportedFileTypeBeforeTrustInspection()
    {
        string path = Path.Combine(root, "setup.ps1");
        await File.WriteAllTextAsync(path, "Write-Output unsafe");
        FakeAuthenticodeTrustService authenticode = new(
            OfflineInstallerSignatureStatus.Trusted,
            "CN=Trusted Vendor",
            new string('C', 64));
        WindowsOfflineInstallerInspectionService service = new(authenticode);

        await Assert.ThrowsAsync<ReplicaSnapshotException>(() =>
            service.InspectAsync(path, CancellationToken.None));

        Assert.Equal(0, authenticode.CallCount);
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

    private string CreatePortableExecutable(string fileName, ushort machine)
    {
        byte[] content = new byte[128];
        content[0] = (byte)'M';
        content[1] = (byte)'Z';
        BitConverter.GetBytes(64).CopyTo(content, 60);
        content[64] = (byte)'P';
        content[65] = (byte)'E';
        BitConverter.GetBytes(machine).CopyTo(content, 68);
        string path = Path.Combine(root, fileName);
        File.WriteAllBytes(path, content);
        return path;
    }

    private sealed class FakeAuthenticodeTrustService(
        OfflineInstallerSignatureStatus status,
        string? publisher,
        string? certificateSha256) : IAuthenticodeTrustService
    {
        public int CallCount { get; private set; }

        public Task<AuthenticodeInspection> InspectAsync(
            string filePath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(new AuthenticodeInspection(
                status,
                publisher,
                certificateSha256,
                status.ToString()));
        }
    }
}
