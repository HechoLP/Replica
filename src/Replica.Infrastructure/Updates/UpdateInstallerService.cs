using System.Diagnostics;
using Replica.Core.Services;
using Replica.Core.Updates;
using Replica.Infrastructure.Portable;

namespace Replica.Infrastructure.Updates;

public interface IUpdateProcessLauncher
{
    bool StartInstaller(string installerPath);
}

public sealed class UpdateInstallerService : IUpdateInstallerService
{
    private readonly IFileHashService hashes;
    private readonly IReplicaPathProvider paths;
    private readonly IUpdateProcessLauncher processLauncher;

    public UpdateInstallerService(
        IReplicaPathProvider paths,
        IFileHashService hashes,
        IUpdateProcessLauncher processLauncher)
    {
        this.paths = paths;
        this.hashes = hashes;
        this.processLauncher = processLauncher;
    }

    public async Task<UpdateInstallerLaunchResult> LaunchAsync(
        UpdateInstallerLaunchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.UserApproved)
        {
            throw new UpdateDownloadException(
                UpdateDownloadErrorCode.ApprovalRequired,
                "Installer launch requires explicit user approval.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        string installerPath = Path.GetFullPath(request.Download.InstallerPath);
        ValidateInstallerPath(installerPath);
        await using FileStream executionLock = new(
            installerPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        FileInfo installer = new(installerPath);
        if (installer.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            installer.Length != request.Download.FileSize)
        {
            throw new UpdateDownloadException(
                UpdateDownloadErrorCode.InvalidAsset,
                "The downloaded installer changed before launch.");
        }

        string hash = await hashes.ComputeSha256Async(installerPath, cancellationToken)
            .ConfigureAwait(false);
        if (!hash.Equals(request.Download.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateDownloadException(
                UpdateDownloadErrorCode.ChecksumMismatch,
                "The downloaded installer failed its pre-launch SHA-256 check.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        installer.Refresh();
        if (installer.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            installer.Length != request.Download.FileSize)
        {
            throw new UpdateDownloadException(
                UpdateDownloadErrorCode.InvalidAsset,
                "The downloaded installer changed immediately before launch.");
        }

        bool started = processLauncher.StartInstaller(installerPath);
        if (!started)
        {
            throw new UpdateDownloadException(
                UpdateDownloadErrorCode.IoFailure,
                "Windows did not start ReplicaSetup.exe.");
        }

        return new UpdateInstallerLaunchResult(true, installerPath);
    }

    private void ValidateInstallerPath(string installerPath)
    {
        string updateRoot = Path.GetFullPath(Path.Combine(paths.TemporaryDirectory, "Updates"));
        string relative = Path.GetRelativePath(updateRoot, installerPath);
        if (Path.IsPathRooted(relative) ||
            relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            !Path.GetFileName(installerPath).Equals("ReplicaSetup.exe", StringComparison.Ordinal) ||
            PortablePathSafety.ContainsReparsePoint(Path.GetDirectoryName(installerPath)!) ||
            !File.Exists(installerPath))
        {
            throw new UpdateDownloadException(
                UpdateDownloadErrorCode.InvalidAsset,
                "The installer path is outside Replica's update temporary directory.");
        }
    }
}

public sealed class WindowsUpdateProcessLauncher : IUpdateProcessLauncher
{
    public bool StartInstaller(string installerPath)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = installerPath,
            WorkingDirectory = Path.GetDirectoryName(installerPath)!,
            UseShellExecute = true,
        };
        return Process.Start(startInfo) is not null;
    }
}
