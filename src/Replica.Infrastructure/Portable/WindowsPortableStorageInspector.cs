using Replica.Core.Portable;
using Replica.Core.Services;

namespace Replica.Infrastructure.Portable;

public sealed record StorageVolumeInfo(
    DriveType DriveType,
    string FileSystem,
    long AvailableFreeSpace,
    bool IsReady);

public interface IStorageVolumeProbe
{
    StorageVolumeInfo Inspect(string directoryPath);
}

public interface IPortableWriteProbe
{
    Task<bool> CanWriteAsync(string directoryPath, CancellationToken cancellationToken);
}

public interface ICloudFolderLocator
{
    string? FindProvider(string directoryPath);
}

public sealed class WindowsPortableStorageInspector : IPortableStorageInspector
{
    internal const long Fat32MaximumFileSize = uint.MaxValue;

    private readonly ICloudFolderLocator cloudFolders;
    private readonly IStorageVolumeProbe volumes;
    private readonly IPortableWriteProbe writeProbe;

    public WindowsPortableStorageInspector(
        IStorageVolumeProbe volumes,
        IPortableWriteProbe writeProbe,
        ICloudFolderLocator cloudFolders)
    {
        this.volumes = volumes;
        this.writeProbe = writeProbe;
        this.cloudFolders = cloudFolders;
    }

    public async Task<PortableStorageInspection> InspectAsync(
        string directoryPath,
        long requiredBytes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentOutOfRangeException.ThrowIfNegative(requiredBytes);
        cancellationToken.ThrowIfCancellationRequested();

        string fullPath = Path.GetFullPath(directoryPath);
        List<PortableStorageIssue> issues = [];
        if (!Directory.Exists(fullPath))
        {
            issues.Add(new PortableStorageIssue(
                PortableStorageIssueCode.DirectoryMissing,
                "The selected destination directory does not exist.",
                true));
            return new PortableStorageInspection(
                fullPath,
                PortableStorageKind.GeneralFolder,
                "Unknown",
                0,
                null,
                false,
                null,
                issues);
        }

        if (PortablePathSafety.ContainsReparsePoint(fullPath))
        {
            issues.Add(new PortableStorageIssue(
                PortableStorageIssueCode.ReparsePoint,
                "The selected destination traverses a reparse point.",
                true));
        }

        StorageVolumeInfo volume;
        try
        {
            volume = volumes.Inspect(fullPath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            issues.Add(new PortableStorageIssue(
                PortableStorageIssueCode.StorageUnavailable,
                "Storage information could not be read.",
                true));
            return new PortableStorageInspection(
                fullPath,
                PortableStorageKind.GeneralFolder,
                "Unknown",
                0,
                null,
                false,
                null,
                issues);
        }

        if (!volume.IsReady)
        {
            issues.Add(new PortableStorageIssue(
                PortableStorageIssueCode.StorageUnavailable,
                "The selected storage device is not ready.",
                true));
        }

        bool isWritable = false;
        if (!issues.Any(issue => issue.BlocksExport))
        {
            isWritable = await writeProbe.CanWriteAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }

        if (!isWritable)
        {
            issues.Add(new PortableStorageIssue(
                PortableStorageIssueCode.NotWritable,
                "Replica cannot write to the selected destination.",
                true));
        }

        long? maximumFileSize = GetMaximumFileSize(volume.FileSystem);
        if (requiredBytes > volume.AvailableFreeSpace)
        {
            issues.Add(new PortableStorageIssue(
                PortableStorageIssueCode.InsufficientSpace,
                "The selected destination does not have enough free space.",
                true));
        }

        if (maximumFileSize is long maximum && requiredBytes > maximum)
        {
            issues.Add(new PortableStorageIssue(
                PortableStorageIssueCode.FileTooLargeForFileSystem,
                $"The {volume.FileSystem} file system cannot store a file of this size.",
                true));
        }

        string? cloudProvider = cloudFolders.FindProvider(fullPath);
        if (cloudProvider is not null)
        {
            issues.Add(new PortableStorageIssue(
                PortableStorageIssueCode.SynchronizationConflictRisk,
                $"{cloudProvider} may still be synchronizing this folder. Confirm synchronization after export.",
                false));
        }

        return new PortableStorageInspection(
            fullPath,
            GetStorageKind(volume.DriveType, cloudProvider),
            string.IsNullOrWhiteSpace(volume.FileSystem) ? "Unknown" : volume.FileSystem,
            volume.AvailableFreeSpace,
            maximumFileSize,
            isWritable,
            cloudProvider,
            issues,
            volume.DriveType == DriveType.Removable);
    }

    private static long? GetMaximumFileSize(string fileSystem) =>
        fileSystem.Equals("FAT32", StringComparison.OrdinalIgnoreCase)
            ? Fat32MaximumFileSize
            : null;

    private static PortableStorageKind GetStorageKind(DriveType driveType, string? cloudProvider)
    {
        if (cloudProvider is not null)
        {
            return PortableStorageKind.CloudSynchronizedFolder;
        }

        return driveType switch
        {
            DriveType.Removable => PortableStorageKind.RemovableDrive,
            DriveType.Fixed => PortableStorageKind.FixedDrive,
            DriveType.Network => PortableStorageKind.NetworkDrive,
            _ => PortableStorageKind.GeneralFolder,
        };
    }
}

public sealed class WindowsStorageVolumeProbe : IStorageVolumeProbe
{
    public StorageVolumeInfo Inspect(string directoryPath)
    {
        string root = Path.GetPathRoot(Path.GetFullPath(directoryPath))
            ?? throw new IOException("The destination volume could not be resolved.");
        DriveInfo drive = new(root);
        return new StorageVolumeInfo(
            drive.DriveType,
            drive.IsReady ? drive.DriveFormat : "Unknown",
            drive.IsReady ? drive.AvailableFreeSpace : 0,
            drive.IsReady);
    }
}

public sealed class PortableWriteProbe : IPortableWriteProbe
{
    public async Task<bool> CanWriteAsync(string directoryPath, CancellationToken cancellationToken)
    {
        string probePath = Path.Combine(directoryPath, $".replica-write-{Guid.NewGuid():N}.tmp");
        bool successful = false;
        try
        {
            await using FileStream stream = new(
                probePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(new byte[] { 0x52 }, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
            successful = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            successful = false;
        }
        finally
        {
            try
            {
                File.Delete(probePath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                successful = false;
            }
        }

        return successful;
    }
}

public sealed class EnvironmentCloudFolderLocator : ICloudFolderLocator
{
    private static readonly string[] OneDriveVariables =
    [
        "OneDrive",
        "OneDriveConsumer",
        "OneDriveCommercial",
    ];

    public string? FindProvider(string directoryPath)
    {
        string fullPath = Path.GetFullPath(directoryPath);
        foreach (string variable in OneDriveVariables)
        {
            if (IsWithin(fullPath, System.Environment.GetEnvironmentVariable(variable)))
            {
                return "OneDrive";
            }
        }

        string profile = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        if (IsWithin(fullPath, Path.Combine(profile, "Google Drive")) ||
            IsWithin(fullPath, Path.Combine(profile, "My Drive")))
        {
            return "Google Drive";
        }

        return IsWithin(fullPath, Path.Combine(profile, "Dropbox")) ? "Dropbox" : null;
    }

    private static bool IsWithin(string path, string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        string relative = Path.GetRelativePath(Path.GetFullPath(root), path);
        return relative == "." ||
            (!Path.IsPathRooted(relative) &&
             relative != ".." &&
             !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }
}

internal static class PortablePathSafety
{
    public static bool ContainsReparsePoint(string path)
    {
        string fullPath = Path.GetFullPath(path);
        DirectoryInfo? current = new(fullPath);
        while (current is not null)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return true;
            }

            current = current.Parent;
        }

        return false;
    }
}
