using System.Security.Cryptography;
using System.Text;
using Replica.Core.Services;
using Replica.Core.Updates;
using Replica.Infrastructure.Paths;
using Replica.Infrastructure.Portable;
using Replica.Infrastructure.Updates;

namespace Replica.Infrastructure.Tests.Updates;

public sealed class UpdateInstallerServiceTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "Replica-Installer-Launch-Tests",
        Guid.NewGuid().ToString("N"));

    public UpdateInstallerServiceTests()
    {
        Directory.CreateDirectory(root);
    }

    [Fact]
    public async Task LaunchAsync_RevalidatesHashAndStartsInteractiveInstaller()
    {
        ReplicaPathProvider paths = new(root);
        UpdateDownloadResult download = await CreateDownloadAsync(paths);
        FakeProcessLauncher launcher = new();
        UpdateInstallerService service = new(paths, new FileHashService(), launcher);

        UpdateInstallerLaunchResult result = await service.LaunchAsync(
            new UpdateInstallerLaunchRequest(download, UserApproved: true),
            CancellationToken.None);

        Assert.True(result.Started);
        Assert.Equal(download.InstallerPath, launcher.StartedPath);
    }

    [Fact]
    public async Task LaunchAsync_BlocksTamperedInstaller()
    {
        ReplicaPathProvider paths = new(root);
        UpdateDownloadResult download = await CreateDownloadAsync(paths);
        await File.WriteAllTextAsync(download.InstallerPath, "tampered");
        FakeProcessLauncher launcher = new();
        UpdateInstallerService service = new(paths, new FileHashService(), launcher);

        await Assert.ThrowsAsync<UpdateDownloadException>(() => service.LaunchAsync(
            new UpdateInstallerLaunchRequest(download, UserApproved: true),
            CancellationToken.None));

        Assert.Null(launcher.StartedPath);
    }

    [Fact]
    public async Task LaunchAsync_HoldsReadLockAcrossHashAndProcessStart()
    {
        ReplicaPathProvider paths = new(root);
        UpdateDownloadResult download = await CreateDownloadAsync(paths);
        FakeProcessLauncher launcher = new();
        RaceAttemptingHashService hashes = new(download.InstallerPath);
        UpdateInstallerService service = new(paths, hashes, launcher);

        UpdateInstallerLaunchResult result = await service.LaunchAsync(
            new UpdateInstallerLaunchRequest(download, UserApproved: true),
            CancellationToken.None);

        Assert.True(result.Started);
        Assert.True(hashes.ReplacementWasBlocked);
        Assert.Equal(download.InstallerPath, launcher.StartedPath);
    }

    [Fact]
    public async Task LaunchAsync_RequiresExplicitApproval()
    {
        ReplicaPathProvider paths = new(root);
        UpdateDownloadResult download = await CreateDownloadAsync(paths);
        FakeProcessLauncher launcher = new();
        UpdateInstallerService service = new(paths, new FileHashService(), launcher);

        UpdateDownloadException exception = await Assert.ThrowsAsync<UpdateDownloadException>(() =>
            service.LaunchAsync(
                new UpdateInstallerLaunchRequest(download, UserApproved: false),
                CancellationToken.None));

        Assert.Equal(UpdateDownloadErrorCode.ApprovalRequired, exception.Code);
        Assert.Null(launcher.StartedPath);
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

    private static async Task<UpdateDownloadResult> CreateDownloadAsync(ReplicaPathProvider paths)
    {
        string directory = Path.Combine(paths.TemporaryDirectory, "Updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string installerPath = Path.Combine(directory, "ReplicaSetup.exe");
        byte[] content = Encoding.UTF8.GetBytes("verified installer");
        await File.WriteAllBytesAsync(installerPath, content);
        return new UpdateDownloadResult(
            new GitHubReleaseDetails(
                new SemanticVersion(1, 1, 0),
                "v1.1.0",
                "Replica 1.1.0",
                DateTimeOffset.UtcNow,
                false,
                false,
                "notes",
                new Uri("https://github.com/HechoLP/Replica/releases/tag/v1.1.0"),
                []),
            installerPath,
            content.Length,
            Convert.ToHexString(SHA256.HashData(content)),
            UpdateChecksumStatus.Verified,
            DateTimeOffset.UtcNow);
    }

    private sealed class FakeProcessLauncher : IUpdateProcessLauncher
    {
        public string? StartedPath { get; private set; }

        public bool StartInstaller(string installerPath)
        {
            StartedPath = installerPath;
            return true;
        }
    }

    private sealed class RaceAttemptingHashService(string installerPath) : IFileHashService
    {
        private readonly FileHashService inner = new();

        public bool ReplacementWasBlocked { get; private set; }

        public async Task<string> ComputeSha256Async(
            string filePath,
            CancellationToken cancellationToken)
        {
            try
            {
                byte[] replacement = new byte[checked((int)new FileInfo(installerPath).Length)];
                await File.WriteAllBytesAsync(installerPath, replacement, cancellationToken);
            }
            catch (IOException)
            {
                ReplacementWasBlocked = true;
            }

            return await inner.ComputeSha256Async(filePath, cancellationToken);
        }
    }
}
