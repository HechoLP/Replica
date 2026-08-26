using System.IO.Compression;
using System.Security.Cryptography;
using Replica.Core.Services;
using Replica.Core.Snapshots;

namespace Replica.Infrastructure.Snapshots;

public sealed class OfflineInstallerExportService : IOfflineInstallerExportService
{
    private readonly IOfflineInstallerInspectionService inspectionService;
    private readonly ISnapshotReader snapshotReader;

    public OfflineInstallerExportService(
        ISnapshotReader snapshotReader,
        IOfflineInstallerInspectionService inspectionService)
    {
        this.snapshotReader = snapshotReader;
        this.inspectionService = inspectionService;
    }

    public async Task<OfflineInstallerExportResult> ExportAsync(
        string snapshotPath,
        Guid expectedSnapshotId,
        string expectedSnapshotSha256,
        string destinationDirectory,
        ReadOnlyMemory<char> password,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSnapshotSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        string fullSnapshotPath = Path.GetFullPath(snapshotPath);
        string fullDestination = ValidateDestination(destinationDirectory);
        ReplicaSnapshotReadResult snapshot = await snapshotReader.ReadAsync(
            new ReplicaSnapshotReadRequest(fullSnapshotPath, password),
            progress: null,
            cancellationToken).ConfigureAwait(false);
        if (snapshot.Manifest.SnapshotType != SnapshotType.OfflineRecoveryPack ||
            snapshot.Manifest.SnapshotId != expectedSnapshotId ||
            snapshot.Recovery.OfflineInstallers.Count == 0)
        {
            throw new ReplicaSnapshotException(
                "The reviewed snapshot does not contain exportable offline installers.");
        }

        Dictionary<string, ReplicaChecksum> checksums = snapshot.Checksums.ToDictionary(
            checksum => checksum.EntryPath,
            StringComparer.Ordinal);
        Dictionary<string, ReplicaArtifact> artifacts = snapshot.Manifest.Metadata.Artifacts.ToDictionary(
            artifact => artifact.ArchivePath,
            StringComparer.Ordinal);
        long requiredBytes = snapshot.Recovery.OfflineInstallers.Sum(installer =>
            installer.ExpectedSize ?? throw new ReplicaSnapshotException(
                "An offline installer lacks reviewed size metadata."));
        EnsureCapacity(fullDestination, requiredBytes);

        List<ExportedOfflineInstaller> exported = [];
        List<string> createdPaths = [];
        byte[]? encryptionKey = null;
        try
        {
            if (snapshot.Manifest.Encryption is not null)
            {
                encryptionKey = SnapshotCrypto.VerifyManifestAndDeriveKey(snapshot.Manifest, password);
            }

            await using FileStream archiveStream = new(
                fullSnapshotPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            string archiveSha256 = Convert.ToHexString(
                await SHA256.HashDataAsync(archiveStream, cancellationToken).ConfigureAwait(false));
            if (!FixedHashEquals(archiveSha256, expectedSnapshotSha256))
            {
                throw new ReplicaSnapshotException(
                    "The Offline Recovery Pack changed after it was reviewed.");
            }

            archiveStream.Position = 0;
            using ZipArchive archive = new(archiveStream, ZipArchiveMode.Read, leaveOpen: false);
            for (int index = 0; index < snapshot.Recovery.OfflineInstallers.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReplicaOfflineInstaller installer = snapshot.Recovery.OfflineInstallers[index];
                string entryPath = SnapshotPathValidator.BuildInstallerEntryPath(installer.ArchivePath);
                ReplicaChecksum checksum = RequireChecksum(checksums, entryPath);
                ReplicaArtifact artifact = RequireArtifact(artifacts, entryPath);
                ValidateReviewedIdentity(installer, artifact, checksum);
                ZipArchiveEntry entry = archive.GetEntry(entryPath) ??
                    throw new ReplicaSnapshotException("An offline installer payload is missing.");
                SnapshotPathValidator.RejectLinkLikeEntry(entry);

                string extension = Path.GetExtension(installer.SourcePath).ToLowerInvariant();
                string preferredName = CreateSafeFileName(
                    Path.GetFileName(installer.SourcePath),
                    installer.DisplayName,
                    extension,
                    index + 1);
                string destinationPath = GetAvailableDestination(fullDestination, preferredName);
                string temporaryPath = Path.Combine(
                    fullDestination,
                    $".replica-export-{Guid.NewGuid():N}{extension}");
                try
                {
                    EnsureDestinationStillSafe(fullDestination);
                    await using (FileStream output = new(
                                     temporaryPath,
                                     FileMode.CreateNew,
                                     FileAccess.Write,
                                     FileShare.None,
                                     128 * 1024,
                                     FileOptions.Asynchronous | FileOptions.WriteThrough))
                    {
                        await ReplicaSnapshotReader.CopyPlaintextEntryAsync(
                            entry,
                            output,
                            snapshot.Manifest,
                            encryptionKey,
                            artifact.Size,
                            cancellationToken).ConfigureAwait(false);
                        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                        output.Flush(flushToDisk: true);
                    }

                    OfflineInstallerInspection inspection = await inspectionService
                        .InspectAsync(temporaryPath, cancellationToken)
                        .ConfigureAwait(false);
                    ValidateExportedInstaller(inspection, installer, artifact);

                    EnsureDestinationStillSafe(fullDestination);
                    File.Move(temporaryPath, destinationPath, overwrite: false);
                    createdPaths.Add(destinationPath);
                    OfflineInstallerInspection publishedInspection = await inspectionService
                        .InspectAsync(destinationPath, cancellationToken)
                        .ConfigureAwait(false);
                    ValidateExportedInstaller(publishedInspection, installer, artifact);
                    exported.Add(new ExportedOfflineInstaller(
                        installer.DisplayName,
                        destinationPath,
                        publishedInspection.FileSize,
                        publishedInspection.Sha256,
                        publishedInspection.Publisher ?? installer.Publisher!));
                }
                finally
                {
                    if (!TryDelete(temporaryPath))
                    {
                        throw new IOException(
                            "A temporary offline installer export could not be removed safely.");
                    }
                }
            }

            return new OfflineInstallerExportResult(fullDestination, exported);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            bool cleanupSucceeded = true;
            foreach (string createdPath in createdPaths)
            {
                cleanupSucceeded &= TryDelete(createdPath);
            }

            if (!cleanupSucceeded)
            {
                throw new IOException(
                    "Offline installer export failed and partial output cleanup is still pending.",
                    exception);
            }

            throw;
        }
        finally
        {
            if (encryptionKey is not null)
            {
                CryptographicOperations.ZeroMemory(encryptionKey);
            }
        }
    }

    private static string ValidateDestination(string destinationDirectory)
    {
        string fullPath = Path.GetFullPath(destinationDirectory);
        if (!Directory.Exists(fullPath) || SnapshotPathValidator.ContainsReparsePoint(fullPath))
        {
            throw new ReplicaSnapshotException("The offline installer export folder is unsafe or unavailable.");
        }

        return fullPath;
    }

    private static void EnsureDestinationStillSafe(string destinationDirectory)
    {
        if (!Directory.Exists(destinationDirectory) ||
            SnapshotPathValidator.ContainsReparsePoint(destinationDirectory))
        {
            throw new ReplicaSnapshotException(
                "The offline installer export folder changed or became unsafe.");
        }
    }

    private static void EnsureCapacity(string destinationDirectory, long requiredBytes)
    {
        try
        {
            string root = Path.GetPathRoot(destinationDirectory) ??
                throw new IOException("The destination volume is unavailable.");
            DriveInfo drive = new(root);
            if (!drive.IsReady || drive.AvailableFreeSpace < requiredBytes + Math.Min(requiredBytes / 20, 512L * 1024 * 1024))
            {
                throw new ReplicaSnapshotException(
                    "The selected folder does not have enough free space for the offline installers.");
            }
        }
        catch (ReplicaSnapshotException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or OverflowException)
        {
            throw new ReplicaSnapshotException(
                "Replica could not verify free space for the offline installer export.",
                exception);
        }
    }

    private static ReplicaChecksum RequireChecksum(
        IReadOnlyDictionary<string, ReplicaChecksum> checksums,
        string entryPath)
    {
        return checksums.TryGetValue(entryPath, out ReplicaChecksum? checksum)
            ? checksum
            : throw new ReplicaSnapshotException("An offline installer checksum is missing.");
    }

    private static ReplicaArtifact RequireArtifact(
        IReadOnlyDictionary<string, ReplicaArtifact> artifacts,
        string entryPath)
    {
        return artifacts.TryGetValue(entryPath, out ReplicaArtifact? artifact)
            ? artifact
            : throw new ReplicaSnapshotException("Offline installer artifact metadata is missing.");
    }

    private static void ValidateReviewedIdentity(
        ReplicaOfflineInstaller installer,
        ReplicaArtifact artifact,
        ReplicaChecksum checksum)
    {
        if (string.IsNullOrWhiteSpace(installer.Publisher) ||
            installer.ExpectedSize != artifact.Size ||
            !FixedHashEquals(installer.ExpectedSha256 ?? string.Empty, artifact.Sha256) ||
            !FixedHashEquals(checksum.Value, artifact.Sha256) ||
            !IsSha256(installer.PublisherCertificateSha256))
        {
            throw new ReplicaSnapshotException(
                "The offline installer does not have complete reviewed identity metadata.");
        }
    }

    private static void ValidateExportedInstaller(
        OfflineInstallerInspection inspection,
        ReplicaOfflineInstaller installer,
        ReplicaArtifact artifact)
    {
        if (!inspection.CanInclude ||
            inspection.FileSize != artifact.Size ||
            !FixedHashEquals(inspection.Sha256, artifact.Sha256) ||
            !FixedHashEquals(
                inspection.PublisherCertificateSha256 ?? string.Empty,
                installer.PublisherCertificateSha256 ?? string.Empty))
        {
            throw new ReplicaSnapshotException(
                "An exported installer no longer matches its reviewed publisher and checksum.");
        }
    }

    private static string CreateSafeFileName(
        string originalName,
        string displayName,
        string extension,
        int sequence)
    {
        string candidate = string.IsNullOrWhiteSpace(originalName)
            ? $"{displayName}{extension}"
            : originalName;
        HashSet<char> invalid = Path.GetInvalidFileNameChars().ToHashSet();
        string safe = new string(candidate
            .Normalize()
            .Select(character => invalid.Contains(character) || char.IsControl(character) ? '_' : character)
            .Take(120)
            .ToArray()).Trim().TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(safe) || IsReservedDeviceName(Path.GetFileNameWithoutExtension(safe)))
        {
            safe = $"Installer-{sequence}{extension}";
        }

        if (!Path.GetExtension(safe).Equals(extension, StringComparison.OrdinalIgnoreCase))
        {
            safe = $"{Path.GetFileNameWithoutExtension(safe)}{extension}";
        }

        return safe;
    }

    private static string GetAvailableDestination(string directory, string preferredName)
    {
        string stem = Path.GetFileNameWithoutExtension(preferredName);
        string extension = Path.GetExtension(preferredName);
        for (int suffix = 1; suffix <= 10_000; suffix++)
        {
            string fileName = suffix == 1
                ? preferredName
                : $"{stem} ({suffix}){extension}";
            string candidate = Path.Combine(directory, fileName);
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new ReplicaSnapshotException("Replica could not choose a non-conflicting export filename.");
    }

    private static bool IsReservedDeviceName(string value)
    {
        return value.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            value.Length == 4 &&
            (value.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
             value.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
            value[3] is >= '1' and <= '9';
    }

    private static bool IsSha256(string? value)
    {
        if (value?.Length != 64)
        {
            return false;
        }

        try
        {
            _ = Convert.FromHexString(value);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool FixedHashEquals(string first, string second)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(first),
                Convert.FromHexString(second));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
