using System.IO.Compression;
using Replica.Core.Snapshots;

namespace Replica.Infrastructure.Snapshots;

internal static class SnapshotPathValidator
{
    public static string NormalizeEntryPath(string entryPath, int maximumDepth)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryPath);

        if (entryPath.Length > 1024 ||
            entryPath.StartsWith("/", StringComparison.Ordinal) ||
            entryPath.StartsWith('\\') ||
            entryPath.Contains('\\') ||
            entryPath.Contains(':') ||
            Path.IsPathFullyQualified(entryPath) ||
            entryPath.Any(char.IsControl))
        {
            throw new ReplicaSnapshotException("The snapshot contains an unsafe entry path.");
        }

        string[] segments = entryPath.Split('/');
        if (segments.Length > maximumDepth ||
            segments.Any(IsUnsafeSegment))
        {
            throw new ReplicaSnapshotException("The snapshot contains an unsafe entry path.");
        }

        return string.Join('/', segments);
    }

    private static bool IsUnsafeSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment) ||
            segment is "." or ".." ||
            segment.EndsWith('.') ||
            segment.EndsWith(' ') ||
            !string.Equals(segment, segment.Normalize(), StringComparison.Ordinal))
        {
            return true;
        }

        string deviceName = segment.Split('.')[0];
        return deviceName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
               deviceName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
               deviceName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
               deviceName.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
               IsNumberedDeviceName(deviceName, "COM") ||
               IsNumberedDeviceName(deviceName, "LPT");
    }

    private static bool IsNumberedDeviceName(string value, string prefix)
    {
        return value.Length == prefix.Length + 1 &&
               value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
               value[^1] is >= '1' and <= '9';
    }

    public static void RejectLinkLikeEntry(ZipArchiveEntry entry)
    {
        const int unixFileTypeMask = 0xF000;
        const int unixSymbolicLink = 0xA000;

        int unixMode = (entry.ExternalAttributes >> 16) & unixFileTypeMask;
        bool isWindowsReparsePoint =
            (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0;

        if (unixMode == unixSymbolicLink || isWindowsReparsePoint)
        {
            throw new ReplicaSnapshotException("The snapshot contains a link or reparse-point entry.");
        }
    }

    public static string BuildFileEntryPath(string archiveRoot, string relativePath)
    {
        string root = NormalizeEntryPath(archiveRoot.Replace('\\', '/').Trim('/'), 30);
        string relative = NormalizeEntryPath(relativePath.Replace('\\', '/').Trim('/'), 30);
        return NormalizeEntryPath($"files/{root}/{relative}", 32);
    }

    public static string BuildInstallerEntryPath(string archivePath)
    {
        string relative = NormalizeEntryPath(archivePath.Replace('\\', '/').Trim('/'), 31);
        return NormalizeEntryPath($"files/{relative}", 32);
    }

    public static bool ContainsReparsePoint(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            return true;
        }

        string? currentDirectory = Directory.Exists(fullPath)
            ? fullPath
            : Path.GetDirectoryName(fullPath);
        while (!string.IsNullOrEmpty(currentDirectory))
        {
            if ((File.GetAttributes(currentDirectory) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }

            string? parent = Path.GetDirectoryName(currentDirectory);
            if (string.Equals(parent, currentDirectory, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            currentDirectory = parent;
        }

        return false;
    }
}
