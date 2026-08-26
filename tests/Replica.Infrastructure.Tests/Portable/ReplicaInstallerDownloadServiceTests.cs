using System.Security.Cryptography;
using System.Text;
using Replica.Core.Portable;
using Replica.Core.Services;
using Replica.Infrastructure.Portable;

namespace Replica.Infrastructure.Tests.Portable;

public sealed class ReplicaInstallerDownloadServiceTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "Replica-Installer-Tests",
        Guid.NewGuid().ToString("N"));

    public ReplicaInstallerDownloadServiceTests()
    {
        Directory.CreateDirectory(root);
    }

    [Fact]
    public async Task DownloadAsync_UsesMockedOfficialGitHubReleaseAndVerifiesHash()
    {
        byte[] content = Encoding.UTF8.GetBytes("mock installer bytes");
        string hash = Convert.ToHexString(SHA256.HashData(content));
        FakeReleaseSource source = new(CreateRelease(content.Length, hash), content);
        ReplicaInstallerDownloadService service = new(
            source,
            new WritableStorageInspector(root),
            new FileHashService());

        ReplicaInstallerDownloadResult result = await service.DownloadAsync(
            new ReplicaInstallerDownloadRequest(
                root,
                ReplicaReleaseSelection.LatestStable,
                null,
                UserApproved: true),
            null,
            CancellationToken.None);

        Assert.Equal("v1.2.3", result.VersionTag);
        Assert.Equal(hash, result.Sha256);
        Assert.True(result.HashWasProvidedByRelease);
        Assert.Equal(content, await File.ReadAllBytesAsync(result.InstallerPath));
        Assert.Equal(1, source.OpenCount);
        Assert.Empty(Directory.EnumerateFiles(root, "*.download"));
    }

    [Fact]
    public async Task DownloadAsync_RejectsWithoutExplicitUserApproval()
    {
        byte[] content = Encoding.UTF8.GetBytes("mock installer bytes");
        FakeReleaseSource source = new(CreateRelease(content.Length, null), content);
        ReplicaInstallerDownloadService service = new(
            source,
            new WritableStorageInspector(root),
            new FileHashService());

        await Assert.ThrowsAsync<ReplicaInstallerDownloadException>(() => service.DownloadAsync(
            new ReplicaInstallerDownloadRequest(
                root,
                ReplicaReleaseSelection.LatestStable,
                null,
                UserApproved: false),
            null,
            CancellationToken.None));

        Assert.Equal(0, source.OpenCount);
    }

    [Fact]
    public async Task DownloadAsync_DeletesTemporaryFileWhenReleaseHashDoesNotMatch()
    {
        byte[] content = Encoding.UTF8.GetBytes("mock installer bytes");
        FakeReleaseSource source = new(CreateRelease(content.Length, new string('0', 64)), content);
        ReplicaInstallerDownloadService service = new(
            source,
            new WritableStorageInspector(root),
            new FileHashService());

        ReplicaInstallerDownloadException exception = await Assert.ThrowsAsync<ReplicaInstallerDownloadException>(
            () => service.DownloadAsync(
                new ReplicaInstallerDownloadRequest(
                    root,
                    ReplicaReleaseSelection.LatestStable,
                    null,
                    UserApproved: true),
                null,
                CancellationToken.None));

        Assert.Contains("SHA-256", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(root, "ReplicaSetup.exe")));
        Assert.Empty(Directory.EnumerateFiles(root, "*.download"));
    }

    [Fact]
    public async Task DownloadAsync_SelectsRequestedPublishedVersion()
    {
        byte[] content = Encoding.UTF8.GetBytes("mock installer bytes");
        OfficialReplicaRelease selected = CreateRelease(content.Length, null) with
        {
            TagName = "v1.2.2",
            Version = new Version(1, 2, 2),
        };
        FakeReleaseSource source = new(selected, content);
        ReplicaInstallerDownloadService service = new(
            source,
            new WritableStorageInspector(root),
            new FileHashService());

        ReplicaInstallerDownloadResult result = await service.DownloadAsync(
            new ReplicaInstallerDownloadRequest(
                root,
                ReplicaReleaseSelection.SpecificVersion,
                "v1.2.2",
                UserApproved: true),
            null,
            CancellationToken.None);

        Assert.Equal("v1.2.2", result.VersionTag);
    }

    [Fact]
    public async Task DownloadAsync_RejectsTemporaryPathReplacementBeforePublication()
    {
        byte[] content = Encoding.UTF8.GetBytes("mock installer bytes");
        string hash = Convert.ToHexString(SHA256.HashData(content));
        FakeReleaseSource source = new(CreateRelease(content.Length, hash), content);
        ReplicaInstallerDownloadService service = new(
            source,
            new WritableStorageInspector(root),
            new ReplacingHashService(content.Length));

        await Assert.ThrowsAsync<ReplicaInstallerDownloadException>(() => service.DownloadAsync(
            new ReplicaInstallerDownloadRequest(
                root,
                ReplicaReleaseSelection.LatestStable,
                null,
                UserApproved: true),
            null,
            CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(root, "ReplicaSetup.exe")));
        Assert.Empty(Directory.EnumerateFiles(root, "*.download"));
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

    private static OfficialReplicaRelease CreateRelease(long size, string? hash) => new(
        new Version(1, 2, 3),
        "v1.2.3",
        new Uri("https://github.com/HechoLP/Replica/releases/tag/v1.2.3"),
        IsPrerelease: false,
        IsDraft: false,
        DateTimeOffset.Parse("2026-08-10T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
        [
            new OfficialReleaseAsset(
                "ReplicaSetup.exe",
                new Uri("https://github.com/HechoLP/Replica/releases/download/v1.2.3/ReplicaSetup.exe"),
                size,
                hash),
        ]);

    private sealed class FakeReleaseSource(
        OfficialReplicaRelease release,
        byte[] content) : IOfficialReleaseSource
    {
        public int OpenCount { get; private set; }

        public Task<IReadOnlyList<OfficialReplicaRelease>> GetReleasesAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<OfficialReplicaRelease>>([release]);
        }

        public Task<Stream> OpenAssetStreamAsync(
            OfficialReleaseAsset asset,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenCount++;
            return Task.FromResult<Stream>(new MemoryStream(content, writable: false));
        }
    }

    private sealed class WritableStorageInspector(string directory) : IPortableStorageInspector
    {
        public Task<PortableStorageInspection> InspectAsync(
            string directoryPath,
            long requiredBytes,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new PortableStorageInspection(
                directory,
                PortableStorageKind.FixedDrive,
                "NTFS",
                long.MaxValue,
                null,
                true,
                null,
                []));
        }
    }

    private sealed class ReplacingHashService(long replacementSize) : IFileHashService
    {
        private int calls;

        public async Task<string> ComputeSha256Async(
            string filePath,
            CancellationToken cancellationToken)
        {
            byte[] content = await File.ReadAllBytesAsync(filePath, cancellationToken);
            if (Interlocked.Increment(ref calls) == 1)
            {
                byte[] replacement = Enumerable.Repeat((byte)'X', checked((int)replacementSize)).ToArray();
                File.Delete(filePath);
                await File.WriteAllBytesAsync(filePath, replacement, cancellationToken);
            }

            return Convert.ToHexString(SHA256.HashData(content));
        }
    }
}
