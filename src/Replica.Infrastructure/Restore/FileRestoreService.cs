using System.Security.Cryptography;
using Replica.Core.Execution;
using Replica.Core.Services;

namespace Replica.Infrastructure.Restore;

public sealed class FileRestoreService : IFileRestoreService
{
    private const long AbsoluteMaximumFileBytes = 8L * 1024 * 1024 * 1024;
    private const int BufferSize = 128 * 1024;

    public async Task<FileRestoreResult> RestoreAsync(
        FileRestoreRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        cancellationToken.ThrowIfCancellationRequested();

        string snapshotRoot = Path.GetFullPath(request.SnapshotRootDirectory);
        string sourcePath = GetContainedSourcePath(snapshotRoot, request.SourceRelativePath);
        string destinationPath = GetApprovedDestinationPath(
            request.DestinationPath,
            request.ApprovedDestinationRoots);
        ValidateExistingFile(sourcePath, request.MaximumFileBytes, "SnapshotSourceUnsafe");

        string finalDestination = destinationPath;
        bool overwrite = false;
        if (File.Exists(destinationPath))
        {
            RejectReparsePoint(destinationPath, "DestinationReparsePoint");
            switch (request.ConflictBehavior)
            {
                case RestoreFileConflictBehavior.KeepExisting:
                    return Skipped("ExistingFileKept", destinationPath, false);
                case RestoreFileConflictBehavior.PromptForEachConflict:
                    return Skipped("FileConflictRequiresConfirmation", destinationPath, true);
                case RestoreFileConflictBehavior.KeepNewest:
                    if (File.GetLastWriteTimeUtc(destinationPath) >= File.GetLastWriteTimeUtc(sourcePath))
                    {
                        return Skipped("NewestExistingFileKept", destinationPath, false);
                    }

                    overwrite = true;
                    break;
                case RestoreFileConflictBehavior.RenameAndKeepBoth:
                    finalDestination = GetAvailableSiblingPath(destinationPath);
                    break;
                case RestoreFileConflictBehavior.OverwriteWithSnapshot:
                    overwrite = true;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(request),
                        request.ConflictBehavior,
                        null);
            }
        }

        string parentDirectory = Path.GetDirectoryName(finalDestination)!;
        EnsureApprovedDirectory(parentDirectory, request.ApprovedDestinationRoots);
        string? backupPath = null;
        if (overwrite)
        {
            backupPath = await BackupExistingFileAsync(
                destinationPath,
                request.RollbackDirectory,
                cancellationToken).ConfigureAwait(false);
        }

        string temporaryPath = Path.Combine(
            parentDirectory,
            $".{Path.GetFileName(finalDestination)}.{Guid.NewGuid():N}.replica.tmp");
        try
        {
            string actualHash = await CopyAndHashAsync(
                sourcePath,
                temporaryPath,
                request.MaximumFileBytes,
                cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actualHash),
                    Convert.FromHexString(request.ExpectedSha256)))
            {
                return new FileRestoreResult(
                    RestoreExecutionState.Failed,
                    "RestoredFileHashMismatch",
                    null,
                    backupPath,
                    false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            EnsureApprovedDirectory(parentDirectory, request.ApprovedDestinationRoots);
            if (overwrite)
            {
                RejectReparsePoint(finalDestination, "DestinationReparsePoint");
                File.Replace(temporaryPath, finalDestination, null, ignoreMetadataErrors: false);
            }
            else
            {
                if (File.Exists(finalDestination))
                {
                    throw new IOException("A destination file appeared during restore.");
                }

                File.Move(temporaryPath, finalDestination, overwrite: false);
            }

            return new FileRestoreResult(
                RestoreExecutionState.Succeeded,
                overwrite ? "FileAtomicallyReplaced" : "FileRestored",
                finalDestination,
                backupPath,
                false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return new FileRestoreResult(
                RestoreExecutionState.Failed,
                "FileRestoreFailed",
                null,
                backupPath,
                false);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private static void ValidateRequest(FileRestoreRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.SnapshotRootDirectory) ||
            string.IsNullOrWhiteSpace(request.SourceRelativePath) ||
            string.IsNullOrWhiteSpace(request.DestinationPath) ||
            request.ApprovedDestinationRoots is null or { Count: 0 } ||
            request.ApprovedDestinationRoots.Any(string.IsNullOrWhiteSpace) ||
            !request.IsExplicitlySelected ||
            !Enum.IsDefined(request.ConflictBehavior) ||
            request.MaximumFileBytes is <= 0 or > AbsoluteMaximumFileBytes ||
            string.IsNullOrWhiteSpace(request.RollbackDirectory) ||
            request.ExpectedSha256 is not { Length: 64 } hash ||
            !hash.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("The file restore request is invalid.", nameof(request));
        }

        if (Path.IsPathFullyQualified(request.SourceRelativePath) ||
            request.SourceRelativePath.Contains(':') ||
            request.SourceRelativePath.Any(char.IsControl) ||
            request.SourceRelativePath.Replace('\\', '/').Split('/').Any(segment =>
                string.IsNullOrWhiteSpace(segment) || segment is "." or ".."))
        {
            throw new ArgumentException("The snapshot source path is unsafe.", nameof(request));
        }
    }

    private static string GetContainedSourcePath(string root, string relativePath)
    {
        if (!Directory.Exists(root))
        {
            throw new ArgumentException("The snapshot source root does not exist.", nameof(root));
        }

        RejectReparsePoint(root, "SnapshotRootReparsePoint");
        string rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The snapshot source path escaped its root.", nameof(relativePath));
        }

        return fullPath;
    }

    private static string GetApprovedDestinationPath(
        string destinationPath,
        IReadOnlyList<string> approvedRoots)
    {
        if (!Path.IsPathFullyQualified(destinationPath))
        {
            throw new ArgumentException("The destination path must be fully qualified.", nameof(destinationPath));
        }

        string fullPath = Path.GetFullPath(destinationPath);
        bool approved = approvedRoots.Any(root => IsContained(fullPath, Path.GetFullPath(root)));
        if (!approved)
        {
            throw new ArgumentException("The destination path is outside approved roots.", nameof(destinationPath));
        }

        return fullPath;
    }

    private static bool IsContained(string path, string root)
    {
        string rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) &&
            !path.Equals(root, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureApprovedDirectory(
        string directory,
        IReadOnlyList<string> approvedRoots)
    {
        string fullDirectory = Path.GetFullPath(directory);
        string? approvedRoot = approvedRoots
            .Select(Path.GetFullPath)
            .FirstOrDefault(root =>
                fullDirectory.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                IsContained(fullDirectory, root));
        if (approvedRoot is null || !Directory.Exists(approvedRoot))
        {
            throw new IOException("The approved destination root is unavailable.");
        }

        RejectReparsePoint(approvedRoot, "DestinationRootReparsePoint");
        Directory.CreateDirectory(fullDirectory);
        RejectReparsePoint(fullDirectory, "DestinationDirectoryReparsePoint");
    }

    private static void ValidateExistingFile(string path, long maximumBytes, string reasonCode)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(reasonCode, path);
        }

        RejectReparsePoint(path, reasonCode);
        if (new FileInfo(path).Length > maximumBytes)
        {
            throw new IOException("The restore source exceeds the configured file size limit.");
        }
    }

    private static void RejectReparsePoint(string path, string reasonCode)
    {
        string? current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(reasonCode);
            }

            string? parent = Path.GetDirectoryName(current);
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase) ||
                parent is null ||
                !Directory.Exists(parent))
            {
                break;
            }

            current = parent;
        }
    }

    private static async Task<string> BackupExistingFileAsync(
        string sourcePath,
        string rollbackDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(rollbackDirectory);
        RejectReparsePoint(rollbackDirectory, "RollbackDirectoryReparsePoint");
        string fileNameHash = Convert.ToHexString(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sourcePath)))[..16];
        string backupPath = Path.Combine(
            rollbackDirectory,
            $"{fileNameHash}-{Path.GetFileName(sourcePath)}.bak");
        await using FileStream source = new(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using FileStream backup = new(
            backupPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await source.CopyToAsync(backup, BufferSize, cancellationToken).ConfigureAwait(false);
        await backup.FlushAsync(cancellationToken).ConfigureAwait(false);
        backup.Flush(flushToDisk: true);
        return backupPath;
    }

    private static async Task<string> CopyAndHashAsync(
        string sourcePath,
        string temporaryPath,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        await using FileStream source = new(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using FileStream destination = new(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[BufferSize];
        long total = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total = checked(total + read);
            if (total > maximumBytes)
            {
                throw new IOException("The restore source exceeded its size limit while copying.");
            }

            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        destination.Flush(flushToDisk: true);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string GetAvailableSiblingPath(string destinationPath)
    {
        string directory = Path.GetDirectoryName(destinationPath)!;
        string name = Path.GetFileNameWithoutExtension(destinationPath);
        string extension = Path.GetExtension(destinationPath);
        for (int index = 1; index <= 10_000; index++)
        {
            string candidate = Path.Combine(directory, $"{name} (Replica {index}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("A safe conflict filename could not be allocated.");
    }

    private static FileRestoreResult Skipped(
        string reasonCode,
        string path,
        bool confirmation)
    {
        return new FileRestoreResult(
            RestoreExecutionState.Skipped,
            reasonCode,
            path,
            null,
            confirmation);
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A later bounded temporary-file sweep can remove this sibling file.
        }
        catch (UnauthorizedAccessException)
        {
            // Cleanup failure is reported by diagnostics without exposing the path.
        }
    }
}
