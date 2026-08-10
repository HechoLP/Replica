using System.Security.Cryptography;
using System.Text;
using Replica.Core.Portable;
using Replica.Core.Services;
using Replica.Core.Updates;
using Replica.Infrastructure.Paths;
using Replica.Infrastructure.Portable;
using Replica.Infrastructure.Updates;

namespace Replica.Infrastructure.Tests.Updates;

public sealed class GitHubUpdateDownloadServiceTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "Replica-Update-Tests",
        Guid.NewGuid().ToString("N"));

    public GitHubUpdateDownloadServiceTests()
    {
        Directory.CreateDirectory(root);
    }

    [Fact]
    public async Task DownloadAsync_VerifiesChecksumAsset()
    {
        byte[] installer = Encoding.UTF8.GetBytes("installer bytes");
        string hash = Convert.ToHexString(SHA256.HashData(installer));
        byte[] checksum = Encoding.UTF8.GetBytes($"{hash}  ReplicaSetup.exe\n");
        GitHubReleaseDetails release = Release(installer.Length, checksum.Length);
        GitHubUpdateDownloadService service = CreateService(
            new FakeReleaseSource(installer, checksum));

        UpdateDownloadResult result = await service.DownloadAsync(
            new UpdateDownloadRequest(release, UserApproved: true),
            null,
            CancellationToken.None);

        Assert.Equal(UpdateChecksumStatus.Verified, result.ChecksumStatus);
        Assert.Equal(hash, result.Sha256);
        Assert.Equal(installer, await File.ReadAllBytesAsync(result.InstallerPath));
    }

    [Fact]
    public async Task DownloadAsync_DeletesTemporaryFilesWhenChecksumMismatches()
    {
        byte[] installer = Encoding.UTF8.GetBytes("installer bytes");
        byte[] checksum = Encoding.UTF8.GetBytes($"{new string('0', 64)}  ReplicaSetup.exe\n");
        GitHubUpdateDownloadService service = CreateService(
            new FakeReleaseSource(installer, checksum));

        UpdateDownloadException exception = await Assert.ThrowsAsync<UpdateDownloadException>(() =>
            service.DownloadAsync(
                new UpdateDownloadRequest(
                    Release(installer.Length, checksum.Length),
                    UserApproved: true),
                null,
                CancellationToken.None));

        Assert.Equal(UpdateDownloadErrorCode.ChecksumMismatch, exception.Code);
        AssertNoUpdateSessions();
    }

    [Fact]
    public async Task DownloadAsync_AllowsMissingChecksumWithExplicitStatus()
    {
        byte[] installer = Encoding.UTF8.GetBytes("installer bytes");
        GitHubUpdateDownloadService service = CreateService(
            new FakeReleaseSource(installer, null));

        UpdateDownloadResult result = await service.DownloadAsync(
            new UpdateDownloadRequest(
                Release(installer.Length, checksumSize: null),
                UserApproved: true),
            null,
            CancellationToken.None);

        Assert.Equal(UpdateChecksumStatus.Unavailable, result.ChecksumStatus);
    }

    [Fact]
    public async Task DownloadAsync_CancellationRemovesTemporarySession()
    {
        byte[] installer = Encoding.UTF8.GetBytes("installer bytes");
        GitHubUpdateDownloadService service = CreateService(
            new FakeReleaseSource(installer, null));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DownloadAsync(
            new UpdateDownloadRequest(
                Release(installer.Length, checksumSize: null),
                UserApproved: true),
            null,
            cancellation.Token));

        AssertNoUpdateSessions();
    }

    [Fact]
    public async Task DownloadAsync_ReportsTimeoutAndRemovesTemporarySession()
    {
        GitHubUpdateDownloadService service = CreateService(
            new BlockingReleaseSource(),
            new UpdateDownloadOptions(1024, 1024, TimeSpan.FromMilliseconds(20)));

        UpdateDownloadException exception = await Assert.ThrowsAsync<UpdateDownloadException>(() =>
            service.DownloadAsync(
                new UpdateDownloadRequest(
                    Release(installerSize: 10, checksumSize: null),
                    UserApproved: true),
                null,
                CancellationToken.None));

        Assert.Equal(UpdateDownloadErrorCode.TimedOut, exception.Code);
        AssertNoUpdateSessions();
    }

    [Fact]
    public async Task DownloadAsync_RejectsForeignAssetUrl()
    {
        byte[] installer = Encoding.UTF8.GetBytes("installer bytes");
        GitHubReleaseDetails release = Release(installer.Length, checksumSize: null) with
        {
            Assets =
            [
                new GitHubReleaseAssetInfo(
                    "ReplicaSetup.exe",
                    new Uri("https://example.com/ReplicaSetup.exe"),
                    installer.Length,
                    null),
            ],
        };
        GitHubUpdateDownloadService service = CreateService(
            new FakeReleaseSource(installer, null));

        UpdateDownloadException exception = await Assert.ThrowsAsync<UpdateDownloadException>(() =>
            service.DownloadAsync(
                new UpdateDownloadRequest(release, UserApproved: true),
                null,
                CancellationToken.None));

        Assert.Equal(UpdateDownloadErrorCode.InvalidAsset, exception.Code);
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

    private GitHubUpdateDownloadService CreateService(
        IOfficialReleaseSource source,
        UpdateDownloadOptions? options = null) => new(
            source,
            new ReplicaPathProvider(root),
            new FileHashService(),
            options ?? UpdateDownloadOptions.Default,
            TimeProvider.System);

    private static GitHubReleaseDetails Release(long installerSize, int? checksumSize)
    {
        List<GitHubReleaseAssetInfo> assets =
        [
            new(
                "ReplicaSetup.exe",
                new Uri("https://github.com/HechoLP/Replica/releases/download/v1.1.0/ReplicaSetup.exe"),
                installerSize,
                null),
        ];
        if (checksumSize is int size)
        {
            assets.Add(new GitHubReleaseAssetInfo(
                "ReplicaSetup.exe.sha256",
                new Uri("https://github.com/HechoLP/Replica/releases/download/v1.1.0/ReplicaSetup.exe.sha256"),
                size,
                null));
        }

        return new GitHubReleaseDetails(
            new SemanticVersion(1, 1, 0),
            "v1.1.0",
            "Replica 1.1.0",
            DateTimeOffset.Parse(
                "2026-08-10T00:00:00Z",
                System.Globalization.CultureInfo.InvariantCulture),
            false,
            false,
            "Release notes",
            new Uri("https://github.com/HechoLP/Replica/releases/tag/v1.1.0"),
            assets);
    }

    private void AssertNoUpdateSessions()
    {
        string updateRoot = Path.Combine(root, "Replica", "Temp", "Updates");
        Assert.True(!Directory.Exists(updateRoot) || !Directory.EnumerateDirectories(updateRoot).Any());
    }

    private sealed class FakeReleaseSource(byte[] installer, byte[]? checksum) : IOfficialReleaseSource
    {
        public Task<IReadOnlyList<OfficialReplicaRelease>> GetReleasesAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Stream> OpenAssetStreamAsync(
            OfficialReleaseAsset asset,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] content = asset.Name.Equals("ReplicaSetup.exe", StringComparison.Ordinal)
                ? installer
                : checksum ?? throw new InvalidOperationException();
            return Task.FromResult<Stream>(new MemoryStream(content, writable: false));
        }
    }

    private sealed class BlockingReleaseSource : IOfficialReleaseSource
    {
        public Task<IReadOnlyList<OfficialReplicaRelease>> GetReleasesAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Stream> OpenAssetStreamAsync(
            OfficialReleaseAsset asset,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<Stream>(new BlockingStream());
        }
    }

    private sealed class BlockingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
