using Replica.Core.Portable;
using Replica.Core.Services;

namespace Replica.Infrastructure.Portable;

public sealed class PortableSnapshotExporter : IPortableSnapshotExporter
{
    private const int CopyBufferSize = 128 * 1024;
    private static readonly HashSet<string> ReservedFileNames = new(
        new[] { "CON", "PRN", "AUX", "NUL" }
            .Concat(Enumerable.Range(1, 9).Select(number => $"COM{number}"))
            .Concat(Enumerable.Range(1, 9).Select(number => $"LPT{number}")),
        StringComparer.OrdinalIgnoreCase);

    private readonly IFileHashService hashes;
    private readonly IPortableStorageInspector storageInspector;
    private readonly TimeProvider timeProvider;

    public PortableSnapshotExporter(
        IPortableStorageInspector storageInspector,
        IFileHashService hashes,
        TimeProvider timeProvider)
    {
        this.storageInspector = storageInspector;
        this.hashes = hashes;
        this.timeProvider = timeProvider;
    }

    public async Task<PortableSnapshotExportResult> ExportAsync(
        PortableSnapshotExportRequest request,
        IProgress<PortableExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string sourcePath = ValidateSource(request.SourceSnapshotPath);
        string fileName = ValidateFileName(request.FileName);
        long sourceLength = new FileInfo(sourcePath).Length;

        progress?.Report(new PortableExportProgress(PortableExportStage.Inspecting, 0, sourceLength));
        PortableStorageInspection inspection = await storageInspector
            .InspectAsync(request.DestinationDirectory, sourceLength, cancellationToken)
            .ConfigureAwait(false);
        if (request.TreatAsSynchronizedFolder && inspection.CloudProvider is null)
        {
            inspection = inspection with
            {
                StorageKind = PortableStorageKind.CloudSynchronizedFolder,
                CloudProvider = "User-selected synchronized folder",
                Issues = inspection.Issues.Concat(
                [
                    new PortableStorageIssue(
                        PortableStorageIssueCode.SynchronizationConflictRisk,
                        "Confirm that synchronization is complete and no conflict copy was created.",
                        false),
                ]).ToArray(),
            };
        }

        if (request.ConfirmExternalStorage)
        {
            inspection = inspection with { IsExternalStorage = true };
        }

        if (!inspection.CanExport)
        {
            string reason = inspection.Issues.First(issue => issue.BlocksExport).Message;
            throw new PortableSnapshotException(reason);
        }

        string destinationPath = Path.Combine(inspection.DirectoryPath, fileName);
        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
        {
            throw new PortableSnapshotException("A file already exists at the selected destination.");
        }

        string temporaryPath = Path.Combine(
            inspection.DirectoryPath,
            $".{Path.GetFileNameWithoutExtension(fileName)}.{Guid.NewGuid():N}.partial");
        string sourceHash;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new PortableExportProgress(PortableExportStage.HashingSource, 0, sourceLength));
            sourceHash = await hashes.ComputeSha256Async(sourcePath, cancellationToken).ConfigureAwait(false);

            progress?.Report(new PortableExportProgress(PortableExportStage.Copying, 0, sourceLength));
            EnsureDestinationStillSafe(inspection.DirectoryPath);
            await CopyAsync(sourcePath, temporaryPath, sourceLength, progress, cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            EnsureTemporaryFileSafe(temporaryPath);
            progress?.Report(new PortableExportProgress(PortableExportStage.Verifying, sourceLength, sourceLength));
            string destinationHash = await hashes.ComputeSha256Async(temporaryPath, cancellationToken)
                .ConfigureAwait(false);
            if (!sourceHash.Equals(destinationHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new PortableSnapshotException("The copied snapshot failed SHA-256 verification.");
            }

            if (new FileInfo(temporaryPath).Length != sourceLength)
            {
                throw new PortableSnapshotException("The copied snapshot size does not match the source.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            EnsureDestinationStillSafe(inspection.DirectoryPath);
            EnsureTemporaryFileSafe(temporaryPath);
            if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
            {
                throw new PortableSnapshotException("A file appeared at the selected destination during export.");
            }

            File.Move(temporaryPath, destinationPath, overwrite: false);
            progress?.Report(new PortableExportProgress(PortableExportStage.Completed, sourceLength, sourceLength));
            return new PortableSnapshotExportResult(
                sourcePath,
                destinationPath,
                sourceLength,
                sourceHash,
                inspection,
                timeProvider.GetUtcNow());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PortableSnapshotException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new PortableSnapshotException("The snapshot could not be exported safely.", exception);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A best-effort cleanup cannot replace the original export result.
            }
        }
    }

    private static string ValidateSource(string sourceSnapshotPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSnapshotPath);
        string fullPath = Path.GetFullPath(sourceSnapshotPath);
        if (!File.Exists(fullPath) ||
            !Path.GetExtension(fullPath).Equals(".replica", StringComparison.OrdinalIgnoreCase))
        {
            throw new PortableSnapshotException("Select an existing .replica snapshot.");
        }

        FileInfo file = new(fullPath);
        if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new PortableSnapshotException("Snapshot reparse points are not supported.");
        }

        return fullPath;
    }

    private static string ValidateFileName(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        string nameWithoutReplicaExtension = Path.GetFileNameWithoutExtension(fileName);
        string deviceStem = nameWithoutReplicaExtension.Split('.', 2)[0];
        if (fileName.Length > 120 ||
            !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal) ||
            fileName.EndsWith(' ') ||
            fileName.EndsWith('.') ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !Path.GetExtension(fileName).Equals(".replica", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(nameWithoutReplicaExtension) ||
            ReservedFileNames.Contains(deviceStem))
        {
            throw new PortableSnapshotException("The snapshot file name is not safe.");
        }

        return fileName;
    }

    private static async Task CopyAsync(
        string sourcePath,
        string destinationPath,
        long totalBytes,
        IProgress<PortableExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using FileStream source = new(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using FileStream destination = new(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        byte[] buffer = new byte[CopyBufferSize];
        long copied = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            copied = checked(copied + read);
            progress?.Report(new PortableExportProgress(PortableExportStage.Copying, copied, totalBytes));
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        destination.Flush(flushToDisk: true);
    }

    private static void EnsureDestinationStillSafe(string directoryPath)
    {
        if (!Directory.Exists(directoryPath) || PortablePathSafety.ContainsReparsePoint(directoryPath))
        {
            throw new PortableSnapshotException("The destination changed during export.");
        }
    }

    private static void EnsureTemporaryFileSafe(string temporaryPath)
    {
        if (!File.Exists(temporaryPath) ||
            new FileInfo(temporaryPath).Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new PortableSnapshotException("The temporary export file changed unexpectedly.");
        }
    }
}
