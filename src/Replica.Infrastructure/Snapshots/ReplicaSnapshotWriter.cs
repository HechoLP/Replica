using System.IO.Compression;
using System.Security.Cryptography;
using Replica.Core.Services;
using Replica.Core.Snapshots;

namespace Replica.Infrastructure.Snapshots;

public sealed class ReplicaSnapshotWriter : ISnapshotWriter
{
    private readonly ISnapshotSelectionEstimator selectionEstimator;
    private readonly ISnapshotReader snapshotReader;

    public ReplicaSnapshotWriter(
        ISnapshotSelectionEstimator selectionEstimator,
        ISnapshotReader snapshotReader)
    {
        this.selectionEstimator = selectionEstimator;
        this.snapshotReader = snapshotReader;
    }

    public async Task<ReplicaSnapshotManifest> WriteAsync(
        ReplicaSnapshotWriteRequest request,
        IProgress<ReplicaSnapshotProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        string destinationPath = Path.GetFullPath(request.DestinationPath);
        string destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new ReplicaSnapshotException("The snapshot destination is invalid.");
        if (!Directory.Exists(destinationDirectory))
        {
            throw new ReplicaSnapshotException("The snapshot destination directory does not exist.");
        }

        if (File.Exists(destinationPath))
        {
            throw new IOException("A snapshot already exists at the requested destination.");
        }

        string operationId = Guid.NewGuid().ToString("N");
        string fileName = Path.GetFileNameWithoutExtension(destinationPath);
        string stagingDirectory = Path.Combine(destinationDirectory, $".{fileName}.{operationId}.staging");
        string temporaryArchivePath = Path.Combine(destinationDirectory, $".{fileName}.{operationId}.replica");
        byte[]? encryptionKey = null;

        try
        {
            progress?.Report(new ReplicaSnapshotProgress(ReplicaSnapshotStage.Estimating, 0, 0, 0, 0));
            ReplicaSelectionEstimate estimate = await selectionEstimator.EstimateAsync(
                request.Recovery.SelectedFolders,
                request.Recovery.OfflineInstallers,
                cancellationToken).ConfigureAwait(false);
            ValidateEstimate(request, estimate);

            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(stagingDirectory);
            progress?.Report(
                new ReplicaSnapshotProgress(
                    ReplicaSnapshotStage.Staging,
                    0,
                    estimate.Files.Count,
                    0,
                    estimate.TotalSize));

            List<ReplicaArtifact> artifacts = [];
            long copiedBytes = 0;
            for (int index = 0; index < estimate.Files.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReplicaFileEstimate file = estimate.Files[index];
                string stagedPath = GetContainedStagingPath(stagingDirectory, file.ArchivePath);
                Directory.CreateDirectory(Path.GetDirectoryName(stagedPath)!);

                string hash = await CopySelectedFileAsync(file.SourcePath, stagedPath, cancellationToken)
                    .ConfigureAwait(false);
                long stagedLength = new FileInfo(stagedPath).Length;
                copiedBytes = checked(copiedBytes + stagedLength);
                artifacts.Add(
                    new ReplicaArtifact(
                        file.ArchivePath,
                        Path.GetFileName(file.SourcePath),
                        stagedLength,
                        hash));
                progress?.Report(
                    new ReplicaSnapshotProgress(
                        ReplicaSnapshotStage.Staging,
                        index + 1,
                        estimate.Files.Count,
                        copiedBytes,
                        estimate.TotalSize));
            }

            IReadOnlyList<ReplicaExclusion> exclusions = request.Exclusions
                .Concat(estimate.Exclusions)
                .Distinct()
                .OrderBy(exclusion => exclusion.Path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(exclusion => exclusion.ReasonCode, StringComparer.Ordinal)
                .ToArray();
            ReplicaSnapshotManifest manifest = CreateManifest(request, artifacts, exclusions);

            if (request.Encryption is not null)
            {
                SnapshotCrypto.PreparedEncryption preparedEncryption = SnapshotCrypto.PrepareManifest(
                    manifest,
                    request.Encryption.Password);
                manifest = preparedEncryption.Manifest;
                encryptionKey = preparedEncryption.Key;
            }

            await WriteRequiredMetadataAsync(
                stagingDirectory,
                request,
                manifest,
                exclusions,
                cancellationToken).ConfigureAwait(false);

            IReadOnlyList<string> stagedFiles = Directory.EnumerateFiles(
                    stagingDirectory,
                    "*",
                    SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            List<ReplicaChecksum> checksums = [];
            for (int index = 0; index < stagedFiles.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string stagedFile = stagedFiles[index];
                string entryPath = ToEntryPath(stagingDirectory, stagedFile);
                string hash = await SnapshotHashing.ComputeFileSha256Async(stagedFile, cancellationToken)
                    .ConfigureAwait(false);
                checksums.Add(new ReplicaChecksum(entryPath, "SHA-256", hash));
                progress?.Report(
                    new ReplicaSnapshotProgress(
                        ReplicaSnapshotStage.Hashing,
                        index + 1,
                        stagedFiles.Count,
                        0,
                        0));
            }

            await WriteJsonAsync(
                stagingDirectory,
                SnapshotEntryNames.Checksums,
                checksums,
                cancellationToken).ConfigureAwait(false);

            await CreateArchiveAsync(
                stagingDirectory,
                temporaryArchivePath,
                manifest,
                encryptionKey,
                progress,
                cancellationToken).ConfigureAwait(false);

            progress?.Report(new ReplicaSnapshotProgress(ReplicaSnapshotStage.Validating, 0, 1, 0, 0));
            ReadOnlyMemory<char> password = request.Encryption?.Password ?? default;
            _ = await snapshotReader.ReadAsync(
                new ReplicaSnapshotReadRequest(temporaryArchivePath, password),
                progress: null,
                cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            DeleteDirectorySafely(stagingDirectory);
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(destinationPath))
            {
                throw new IOException("A snapshot appeared at the requested destination during creation.");
            }

            File.Move(temporaryArchivePath, destinationPath, overwrite: false);
            progress?.Report(new ReplicaSnapshotProgress(ReplicaSnapshotStage.Completed, 1, 1, 0, 0));
            return manifest;
        }
        catch (ReplicaSnapshotException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            throw new ReplicaSnapshotException("Snapshot creation failed while processing selected data.");
        }
        finally
        {
            if (encryptionKey is not null)
            {
                CryptographicOperations.ZeroMemory(encryptionKey);
            }

            try
            {
                DeleteFileIfPresent(temporaryArchivePath);
                DeleteDirectorySafely(stagingDirectory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new ReplicaSnapshotException("Temporary snapshot data could not be removed safely.");
            }
        }
    }

    private static void ValidateRequest(ReplicaSnapshotWriteRequest request)
    {
        if (request.Inventory is null ||
            request.Inventory.Applications is null ||
            request.Inventory.Environment is null ||
            request.Inventory.Environment.Variables is null ||
            request.Inventory.Environment.PathEntries is null ||
            request.Inventory.Windows is null ||
            request.Inventory.Windows.Capabilities is null ||
            request.Inventory.Fonts is null ||
            request.Inventory.Plugins is null ||
            request.Machine is null ||
            request.Machine.Windows is null ||
            request.Machine.Windows.Capabilities is null ||
            request.Recovery is null ||
            request.Recovery.SelectedFolders is null ||
            request.Recovery.OfflineInstallers is null ||
            request.Capabilities is null ||
            request.Exclusions is null)
        {
            throw new ReplicaSnapshotException("Snapshot input metadata is incomplete.");
        }

        if (!string.Equals(Path.GetExtension(request.DestinationPath), ".replica", StringComparison.OrdinalIgnoreCase))
        {
            throw new ReplicaSnapshotException("Snapshot files must use the .replica extension.");
        }

        if (string.IsNullOrWhiteSpace(request.ProductVersion) ||
            string.IsNullOrWhiteSpace(request.Machine.MachineName) ||
            string.IsNullOrWhiteSpace(request.Machine.Windows.Version) ||
            string.IsNullOrWhiteSpace(request.Machine.Architecture) ||
            string.IsNullOrWhiteSpace(request.Machine.Locale) ||
            !WindowsInfoMatches(request.Inventory.Windows, request.Machine.Windows) ||
            !Enum.IsDefined(request.SnapshotType) ||
            request.Capabilities.Any(string.IsNullOrWhiteSpace) ||
            request.Exclusions.Any(exclusion =>
                exclusion is null || string.IsNullOrWhiteSpace(exclusion.ReasonCode)) ||
            request.Recovery.SelectedFolders.Any(folder =>
                folder is null ||
                string.IsNullOrWhiteSpace(folder.SourcePath) ||
                string.IsNullOrWhiteSpace(folder.ArchivePath) ||
                !Enum.IsDefined(folder.Category)) ||
            request.Recovery.OfflineInstallers.Any(installer =>
                installer is null ||
                string.IsNullOrWhiteSpace(installer.SourcePath) ||
                string.IsNullOrWhiteSpace(installer.ArchivePath) ||
                string.IsNullOrWhiteSpace(installer.DisplayName) ||
                string.IsNullOrWhiteSpace(installer.Provenance)) ||
            request.Inventory.Environment.Variables.Any(variable =>
                variable is null || string.IsNullOrWhiteSpace(variable.Name)))
        {
            throw new ReplicaSnapshotException("Snapshot type or recovery metadata is invalid.");
        }

        if (request.Inventory.Environment.Variables.Any(variable => IsSensitiveVariableName(variable.Name)))
        {
            throw new ReplicaSnapshotException("Sensitive environment variables cannot be written to a snapshot.");
        }

        bool hasSelectedFiles = request.Recovery.SelectedFolders.Count > 0;
        bool hasOfflineInstallers = request.Recovery.OfflineInstallers.Count > 0;
        if (request.SnapshotType == SnapshotType.Lightweight && (hasSelectedFiles || hasOfflineInstallers))
        {
            throw new ReplicaSnapshotException("A Lightweight snapshot cannot contain selected files or installers.");
        }

        if (request.SnapshotType == SnapshotType.Recovery && hasOfflineInstallers)
        {
            throw new ReplicaSnapshotException("A Recovery snapshot cannot contain offline installers.");
        }
    }

    private static bool WindowsInfoMatches(ReplicaWindowsInfo left, ReplicaWindowsInfo right)
    {
        return string.Equals(left.Edition, right.Edition, StringComparison.Ordinal) &&
               string.Equals(left.Version, right.Version, StringComparison.Ordinal) &&
               string.Equals(left.Build, right.Build, StringComparison.Ordinal) &&
               string.Equals(left.Architecture, right.Architecture, StringComparison.Ordinal) &&
               string.Equals(left.Locale, right.Locale, StringComparison.Ordinal) &&
               string.Equals(left.TimeZone, right.TimeZone, StringComparison.Ordinal) &&
               left.Capabilities.SequenceEqual(right.Capabilities, StringComparer.Ordinal);
    }

    private static bool IsSensitiveVariableName(string name)
    {
        string normalized = name.Replace('-', '_').ToUpperInvariant();
        return normalized.Contains("PASSWORD", StringComparison.Ordinal) ||
               normalized.Contains("PASSWD", StringComparison.Ordinal) ||
               normalized.Contains("TOKEN", StringComparison.Ordinal) ||
               normalized.Contains("SECRET", StringComparison.Ordinal) ||
               normalized.Contains("API_KEY", StringComparison.Ordinal) ||
               normalized.Contains("PRIVATE_KEY", StringComparison.Ordinal) ||
               normalized.Contains("RECOVERY_KEY", StringComparison.Ordinal) ||
               normalized.Contains("CONNECTION_STRING", StringComparison.Ordinal);
    }

    private static void ValidateEstimate(
        ReplicaSnapshotWriteRequest request,
        ReplicaSelectionEstimate estimate)
    {
        if (estimate is null || estimate.Files is null || estimate.Exclusions is null || estimate.TotalSize < 0)
        {
            throw new ReplicaSnapshotException("The selected-file estimate is invalid.");
        }

        HashSet<string> archivePaths = new(StringComparer.OrdinalIgnoreCase);
        long calculatedTotalSize = 0;
        foreach (ReplicaFileEstimate file in estimate.Files)
        {
            if (file is null || file.Size < 0 ||
                string.IsNullOrWhiteSpace(file.SourcePath) ||
                string.IsNullOrWhiteSpace(file.ArchivePath) ||
                !archivePaths.Add(SnapshotPathValidator.NormalizeEntryPath(file.ArchivePath, 32)) ||
                !IsExplicitlySelected(request.Recovery, file))
            {
                throw new ReplicaSnapshotException("The selected-file estimate contains an unapproved file.");
            }

            try
            {
                calculatedTotalSize = checked(calculatedTotalSize + file.Size);
            }
            catch (OverflowException)
            {
                throw new ReplicaSnapshotException("The selected-file estimate is too large.");
            }
        }

        if (calculatedTotalSize != estimate.TotalSize)
        {
            throw new ReplicaSnapshotException("The selected-file estimate has an inconsistent total size.");
        }
    }

    private static bool IsExplicitlySelected(
        ReplicaRecoveryOptions recovery,
        ReplicaFileEstimate file)
    {
        string sourcePath = Path.GetFullPath(file.SourcePath);
        foreach (ReplicaOfflineInstaller installer in recovery.OfflineInstallers)
        {
            string installerPath = Path.GetFullPath(installer.SourcePath);
            string installerEntryPath = SnapshotPathValidator.BuildInstallerEntryPath(installer.ArchivePath);
            if (string.Equals(sourcePath, installerPath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(file.ArchivePath, installerEntryPath, StringComparison.Ordinal))
            {
                return true;
            }
        }

        foreach (ReplicaSelectedFolder folder in recovery.SelectedFolders)
        {
            string rootPath = Path.GetFullPath(folder.SourcePath);
            string relativePath = Path.GetRelativePath(rootPath, sourcePath);
            if (Path.IsPathRooted(relativePath) ||
                relativePath.Equals("..", StringComparison.Ordinal) ||
                relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            string expectedEntryPath = SnapshotPathValidator.BuildFileEntryPath(
                folder.ArchivePath,
                relativePath);
            if (string.Equals(file.ArchivePath, expectedEntryPath, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static ReplicaSnapshotManifest CreateManifest(
        ReplicaSnapshotWriteRequest request,
        IReadOnlyList<ReplicaArtifact> artifacts,
        IReadOnlyList<ReplicaExclusion> exclusions)
    {
        return new ReplicaSnapshotManifest(
            ReplicaSnapshotManifest.CurrentSchemaVersion,
            request.ProductVersion,
            Guid.NewGuid(),
            request.SnapshotType,
            DateTimeOffset.UtcNow,
            request.Machine.MachineName,
            request.Machine.Windows.Version,
            request.Machine.Architecture,
            request.Machine.Locale,
            request.Capabilities.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            exclusions,
            new ReplicaSnapshotMetadata(request.Machine, artifacts, request.Hardware));
    }

    private static async Task WriteRequiredMetadataAsync(
        string stagingDirectory,
        ReplicaSnapshotWriteRequest request,
        ReplicaSnapshotManifest manifest,
        IReadOnlyList<ReplicaExclusion> exclusions,
        CancellationToken cancellationToken)
    {
        await WriteJsonAsync(stagingDirectory, SnapshotEntryNames.Manifest, manifest, cancellationToken)
            .ConfigureAwait(false);
        await WriteJsonAsync(
            stagingDirectory,
            SnapshotEntryNames.Applications,
            request.Inventory.Applications,
            cancellationToken).ConfigureAwait(false);
        await WriteJsonAsync(
            stagingDirectory,
            SnapshotEntryNames.Environment,
            request.Inventory.Environment,
            cancellationToken).ConfigureAwait(false);
        await WriteJsonAsync(
            stagingDirectory,
            SnapshotEntryNames.Windows,
            request.Inventory.Windows,
            cancellationToken).ConfigureAwait(false);
        await WriteJsonAsync(
            stagingDirectory,
            SnapshotEntryNames.Fonts,
            request.Inventory.Fonts,
            cancellationToken).ConfigureAwait(false);
        await WriteJsonAsync(
            stagingDirectory,
            SnapshotEntryNames.Plugins,
            request.Inventory.Plugins,
            cancellationToken).ConfigureAwait(false);
        await WriteJsonAsync(
            stagingDirectory,
            SnapshotEntryNames.Exclusions,
            exclusions,
            cancellationToken).ConfigureAwait(false);
        await WriteJsonAsync(
            stagingDirectory,
            SnapshotEntryNames.Recovery,
            request.Recovery,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync<T>(
        string stagingDirectory,
        string entryPath,
        T value,
        CancellationToken cancellationToken)
    {
        string outputPath = GetContainedStagingPath(stagingDirectory, entryPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        byte[] json = SnapshotJson.Serialize(value);
        await File.WriteAllBytesAsync(outputPath, json, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> CopySelectedFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        FileAttributes attributes = File.GetAttributes(sourcePath);
        if (SnapshotPathValidator.ContainsReparsePoint(sourcePath) ||
            (attributes & FileAttributes.Directory) != 0)
        {
            throw new ReplicaSnapshotException("A selected file became unsafe before it could be copied.");
        }

        await using FileStream source = new(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using FileStream destination = new(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[128 * 1024];

        while (true)
        {
            int bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, bytesRead);
            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken)
                .ConfigureAwait(false);
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        destination.Flush(flushToDisk: true);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static async Task CreateArchiveAsync(
        string stagingDirectory,
        string temporaryArchivePath,
        ReplicaSnapshotManifest manifest,
        byte[]? encryptionKey,
        IProgress<ReplicaSnapshotProgress>? progress,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> stagedFiles = Directory.EnumerateFiles(
                stagingDirectory,
                "*",
                SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        await using FileStream archiveStream = new(
            temporaryArchivePath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);

        using (ZipArchive archive = new(archiveStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (int index = 0; index < stagedFiles.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string stagedFile = stagedFiles[index];
                string entryPath = ToEntryPath(stagingDirectory, stagedFile);
                CompressionLevel compressionLevel = entryPath.StartsWith("files/", StringComparison.Ordinal)
                    ? CompressionLevel.NoCompression
                    : CompressionLevel.Optimal;
                ZipArchiveEntry entry = archive.CreateEntry(entryPath, compressionLevel);
                entry.LastWriteTime = manifest.CreatedAtUtc;

                await using Stream entryStream = entry.Open();
                await using FileStream source = new(
                    stagedFile,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                if (encryptionKey is not null &&
                    !string.Equals(entryPath, SnapshotEntryNames.Manifest, StringComparison.OrdinalIgnoreCase))
                {
                    await SnapshotCrypto.EncryptAsync(
                        source,
                        entryStream,
                        encryptionKey,
                        manifest,
                        entryPath,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await source.CopyToAsync(entryStream, 128 * 1024, cancellationToken)
                        .ConfigureAwait(false);
                }

                progress?.Report(
                    new ReplicaSnapshotProgress(
                        ReplicaSnapshotStage.Archiving,
                        index + 1,
                        stagedFiles.Count,
                        0,
                        0));
            }
        }

        await archiveStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        archiveStream.Flush(flushToDisk: true);
    }

    private static string GetContainedStagingPath(string stagingDirectory, string entryPath)
    {
        string normalizedEntryPath = SnapshotPathValidator.NormalizeEntryPath(entryPath, 32);
        string fullRoot = Path.GetFullPath(stagingDirectory) + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(
            Path.Combine(stagingDirectory, normalizedEntryPath.Replace('/', Path.DirectorySeparatorChar)));

        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ReplicaSnapshotException("A staged snapshot path escaped its temporary directory.");
        }

        return fullPath;
    }

    private static string ToEntryPath(string stagingDirectory, string stagedFile)
    {
        string relativePath = Path.GetRelativePath(stagingDirectory, stagedFile).Replace('\\', '/');
        return SnapshotPathValidator.NormalizeEntryPath(relativePath, 32);
    }

    private static void DeleteFileIfPresent(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void DeleteDirectorySafely(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        FileAttributes rootAttributes = File.GetAttributes(path);
        if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(path, recursive: false);
            return;
        }

        foreach (string entry in Directory.EnumerateFileSystemEntries(path))
        {
            FileAttributes attributes = File.GetAttributes(entry);
            bool isDirectory = (attributes & FileAttributes.Directory) != 0;
            bool isReparsePoint = (attributes & FileAttributes.ReparsePoint) != 0;

            if (isDirectory && !isReparsePoint)
            {
                DeleteDirectorySafely(entry);
            }
            else if (isDirectory)
            {
                Directory.Delete(entry, recursive: false);
            }
            else
            {
                File.Delete(entry);
            }
        }

        Directory.Delete(path, recursive: false);
    }
}
