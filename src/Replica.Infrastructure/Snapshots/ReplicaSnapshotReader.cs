using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Replica.Core.Services;
using Replica.Core.Snapshots;

namespace Replica.Infrastructure.Snapshots;

public sealed class ReplicaSnapshotReader : ISnapshotReader
{
    private readonly ReplicaSnapshotReadLimits limits;

    public ReplicaSnapshotReader()
        : this(ReplicaSnapshotReadLimits.Default)
    {
    }

    public ReplicaSnapshotReader(ReplicaSnapshotReadLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.MaximumEntryCount <= 0 ||
            limits.MaximumEntrySize <= 0 ||
            limits.MaximumTotalUncompressedSize <= 0 ||
            limits.MaximumCompressionRatio <= 0 ||
            limits.MaximumPathDepth <= 0 ||
            limits.MaximumJsonSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limits));
        }

        this.limits = limits;
    }

    public async Task<ReplicaSnapshotReadResult> ReadAsync(
        ReplicaSnapshotReadRequest request,
        IProgress<ReplicaSnapshotProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        byte[]? encryptionKey = null;

        try
        {
            ValidateSnapshotFile(request.SnapshotPath);
            string snapshotPath = Path.GetFullPath(request.SnapshotPath);
            await using FileStream archiveStream = new(
                snapshotPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using ZipArchive archive = new(archiveStream, ZipArchiveMode.Read, leaveOpen: false);
            IReadOnlyDictionary<string, ZipArchiveEntry> entries = await Task.Run(
                () => InspectArchive(archive),
                cancellationToken).ConfigureAwait(false);

            byte[] manifestBytes = await ReadRawEntryBytesAsync(
                entries[SnapshotEntryNames.Manifest],
                limits.MaximumJsonSize,
                cancellationToken).ConfigureAwait(false);
            ReplicaSnapshotManifest manifest = DeserializeJson<ReplicaSnapshotManifest>(manifestBytes);
            ValidateManifest(manifest);

            if (manifest.Encryption is not null)
            {
                encryptionKey = SnapshotCrypto.VerifyManifestAndDeriveKey(manifest, request.Password);
            }

            byte[] checksumBytes = await ReadPlaintextEntryBytesAsync(
                entries[SnapshotEntryNames.Checksums],
                manifest,
                encryptionKey,
                limits.MaximumJsonSize,
                cancellationToken).ConfigureAwait(false);
            IReadOnlyList<ReplicaChecksum> checksums = DeserializeJson<IReadOnlyList<ReplicaChecksum>>(
                checksumBytes);
            IReadOnlyDictionary<string, ReplicaChecksum> checksumIndex = ValidateChecksumIndex(
                checksums,
                entries);

            int completedEntries = 0;
            foreach ((string entryPath, ZipArchiveEntry entry) in entries.OrderBy(
                         pair => pair.Key,
                         StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(entryPath, SnapshotEntryNames.Checksums, StringComparison.Ordinal))
                {
                    continue;
                }

                string actualHash;
                if (string.Equals(entryPath, SnapshotEntryNames.Manifest, StringComparison.Ordinal))
                {
                    actualHash = Convert.ToHexString(SHA256.HashData(manifestBytes));
                }
                else
                {
                    using HashingWriteStream hashingStream = new();
                    await CopyPlaintextEntryAsync(
                        entry,
                        hashingStream,
                        manifest,
                        encryptionKey,
                        limits.MaximumEntrySize,
                        cancellationToken).ConfigureAwait(false);
                    actualHash = hashingStream.GetHash();
                }

                if (!CryptographicOperations.FixedTimeEquals(
                        Convert.FromHexString(actualHash),
                        Convert.FromHexString(checksumIndex[entryPath].Value)))
                {
                    throw new ReplicaSnapshotException("Snapshot checksum validation failed.");
                }

                completedEntries++;
                progress?.Report(
                    new ReplicaSnapshotProgress(
                        ReplicaSnapshotStage.Validating,
                        completedEntries,
                        entries.Count - 1,
                        0,
                        0));
            }

            IReadOnlyList<ReplicaApplication> applications = await ReadJsonEntryAsync<IReadOnlyList<ReplicaApplication>>(
                entries[SnapshotEntryNames.Applications],
                manifest,
                encryptionKey,
                cancellationToken).ConfigureAwait(false);
            ReplicaEnvironmentInventory environment = await ReadJsonEntryAsync<ReplicaEnvironmentInventory>(
                entries[SnapshotEntryNames.Environment],
                manifest,
                encryptionKey,
                cancellationToken).ConfigureAwait(false);
            ReplicaWindowsInfo windows = await ReadJsonEntryAsync<ReplicaWindowsInfo>(
                entries[SnapshotEntryNames.Windows],
                manifest,
                encryptionKey,
                cancellationToken).ConfigureAwait(false);
            IReadOnlyList<ReplicaFontInfo> fonts = await ReadJsonEntryAsync<IReadOnlyList<ReplicaFontInfo>>(
                entries[SnapshotEntryNames.Fonts],
                manifest,
                encryptionKey,
                cancellationToken).ConfigureAwait(false);
            IReadOnlyList<ReplicaPluginSnapshot> plugins = await ReadJsonEntryAsync<IReadOnlyList<ReplicaPluginSnapshot>>(
                entries[SnapshotEntryNames.Plugins],
                manifest,
                encryptionKey,
                cancellationToken).ConfigureAwait(false);
            IReadOnlyList<ReplicaExclusion> exclusions = await ReadJsonEntryAsync<IReadOnlyList<ReplicaExclusion>>(
                entries[SnapshotEntryNames.Exclusions],
                manifest,
                encryptionKey,
                cancellationToken).ConfigureAwait(false);
            ReplicaRecoveryOptions recovery = await ReadJsonEntryAsync<ReplicaRecoveryOptions>(
                entries[SnapshotEntryNames.Recovery],
                manifest,
                encryptionKey,
                cancellationToken).ConfigureAwait(false);

            ValidateInventory(applications, environment, windows, fonts, plugins);
            ValidateSnapshotSemantics(manifest, windows, exclusions, recovery, entries, checksumIndex);
            ReplicaSnapshotInventory inventory = new(applications, environment, windows, fonts, plugins);
            progress?.Report(new ReplicaSnapshotProgress(ReplicaSnapshotStage.Completed, 1, 1, 0, 0));
            return new ReplicaSnapshotReadResult(
                manifest,
                inventory,
                recovery,
                checksums,
                entries.Keys.Order(StringComparer.Ordinal).ToArray());
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
            exception is InvalidDataException or IOException or JsonException or FormatException or OverflowException or ArgumentException)
        {
            throw new ReplicaSnapshotException("The file is not a valid Replica snapshot.");
        }
        finally
        {
            if (encryptionKey is not null)
            {
                CryptographicOperations.ZeroMemory(encryptionKey);
            }
        }
    }

    private void ValidateSnapshotFile(string snapshotPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotPath);
        if (!string.Equals(Path.GetExtension(snapshotPath), ".replica", StringComparison.OrdinalIgnoreCase))
        {
            throw new ReplicaSnapshotException("Snapshot files must use the .replica extension.");
        }

        if (!File.Exists(snapshotPath))
        {
            throw new ReplicaSnapshotException("The snapshot file was not found.");
        }

        if (SnapshotPathValidator.ContainsReparsePoint(snapshotPath))
        {
            throw new ReplicaSnapshotException("A snapshot cannot be opened through a reparse point.");
        }
    }

    private IReadOnlyDictionary<string, ZipArchiveEntry> InspectArchive(ZipArchive archive)
    {
        if (archive.Entries.Count == 0 || archive.Entries.Count > limits.MaximumEntryCount)
        {
            throw new ReplicaSnapshotException("The snapshot entry count exceeds the configured limit.");
        }

        Dictionary<string, ZipArchiveEntry> entries = new(StringComparer.Ordinal);
        HashSet<string> caseInsensitivePaths = new(StringComparer.OrdinalIgnoreCase);
        long totalUncompressedSize = 0;

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                throw new ReplicaSnapshotException("Directory-only entries are not permitted in a snapshot.");
            }

            string entryPath = SnapshotPathValidator.NormalizeEntryPath(entry.FullName, limits.MaximumPathDepth);
            SnapshotPathValidator.RejectLinkLikeEntry(entry);

            if (!caseInsensitivePaths.Add(entryPath))
            {
                throw new ReplicaSnapshotException("The snapshot contains duplicate or case-colliding entries.");
            }

            if (entry.Length < 0 || entry.Length > limits.MaximumEntrySize)
            {
                throw new ReplicaSnapshotException("A snapshot entry exceeds the configured size limit.");
            }

            totalUncompressedSize = checked(totalUncompressedSize + entry.Length);
            if (totalUncompressedSize > limits.MaximumTotalUncompressedSize)
            {
                throw new ReplicaSnapshotException("The snapshot exceeds the configured total size limit.");
            }

            if (entry.Length > 0)
            {
                double ratio = (double)entry.Length / Math.Max(1, entry.CompressedLength);
                if (ratio > limits.MaximumCompressionRatio)
                {
                    throw new ReplicaSnapshotException("A snapshot entry exceeds the configured compression ratio limit.");
                }
            }

            entries.Add(entryPath, entry);
        }

        foreach (string requiredEntry in SnapshotEntryNames.Required)
        {
            if (!entries.ContainsKey(requiredEntry))
            {
                throw new ReplicaSnapshotException("The snapshot is missing a required entry.");
            }
        }

        return entries;
    }

    private static void ValidateManifest(ReplicaSnapshotManifest manifest)
    {
        if (!string.Equals(
                manifest.SchemaVersion,
                ReplicaSnapshotManifest.CurrentSchemaVersion,
                StringComparison.Ordinal) ||
            manifest.SnapshotId == Guid.Empty ||
            !Enum.IsDefined(manifest.SnapshotType) ||
            !Enum.IsDefined(manifest.SourcePlatform) ||
            manifest.CreatedAtUtc.Offset != TimeSpan.Zero ||
            string.IsNullOrWhiteSpace(manifest.ProductVersion) ||
            string.IsNullOrWhiteSpace(manifest.SourceMachineName) ||
            string.IsNullOrWhiteSpace(manifest.WindowsVersion) ||
            string.IsNullOrWhiteSpace(manifest.Architecture) ||
            string.IsNullOrWhiteSpace(manifest.Locale) ||
            manifest.Capabilities is null ||
            manifest.Exclusions is null ||
            manifest.Metadata is null ||
            manifest.Metadata.Machine is null ||
            manifest.Metadata.Machine.Windows is null ||
            manifest.Metadata.Machine.Windows.Capabilities is null ||
            manifest.Metadata.Artifacts is null ||
            manifest.Metadata.Artifacts.Any(artifact =>
                artifact is null ||
                string.IsNullOrWhiteSpace(artifact.ArchivePath) ||
                string.IsNullOrWhiteSpace(artifact.DisplayName) ||
                string.IsNullOrWhiteSpace(artifact.Sha256) ||
                artifact.Size < 0) ||
            manifest.Capabilities.Any(string.IsNullOrWhiteSpace) ||
            manifest.Exclusions.Any(exclusion =>
                exclusion is null || string.IsNullOrWhiteSpace(exclusion.ReasonCode)) ||
            !string.Equals(
                manifest.SourceMachineName,
                manifest.Metadata.Machine.MachineName,
                StringComparison.Ordinal) ||
            !string.Equals(
                manifest.WindowsVersion,
                manifest.Metadata.Machine.Windows.Version,
                StringComparison.Ordinal) ||
            !string.Equals(
                manifest.Architecture,
                manifest.Metadata.Machine.Architecture,
                StringComparison.Ordinal) ||
            !string.Equals(
                manifest.Locale,
                manifest.Metadata.Machine.Locale,
                StringComparison.Ordinal) ||
            manifest.SourcePlatform != ReplicaPlatformFamily.Windows &&
            (manifest.Metadata.Machine.Platform is null ||
             manifest.Metadata.Machine.Platform.Family != manifest.SourcePlatform))
        {
            throw new ReplicaSnapshotException("The snapshot manifest is invalid or unsupported.");
        }
    }

    private static IReadOnlyDictionary<string, ReplicaChecksum> ValidateChecksumIndex(
        IReadOnlyList<ReplicaChecksum> checksums,
        IReadOnlyDictionary<string, ZipArchiveEntry> entries)
    {
        if (checksums is null || checksums.Count != entries.Count - 1)
        {
            throw new ReplicaSnapshotException("The snapshot checksum index is incomplete.");
        }

        Dictionary<string, ReplicaChecksum> index = new(StringComparer.Ordinal);
        HashSet<string> caseInsensitivePaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (ReplicaChecksum checksum in checksums)
        {
            if (checksum is null)
            {
                throw new ReplicaSnapshotException("The snapshot checksum index is invalid.");
            }

            if (string.IsNullOrWhiteSpace(checksum.EntryPath) ||
                string.IsNullOrWhiteSpace(checksum.Algorithm) ||
                string.IsNullOrWhiteSpace(checksum.Value))
            {
                throw new ReplicaSnapshotException("The snapshot checksum index is invalid.");
            }

            string entryPath = SnapshotPathValidator.NormalizeEntryPath(checksum.EntryPath, 32);
            if (!caseInsensitivePaths.Add(entryPath) ||
                string.Equals(entryPath, SnapshotEntryNames.Checksums, StringComparison.OrdinalIgnoreCase) ||
                !entries.ContainsKey(entryPath) ||
                !string.Equals(checksum.Algorithm, "SHA-256", StringComparison.Ordinal) ||
                checksum.Value.Length != 64)
            {
                throw new ReplicaSnapshotException("The snapshot checksum index is invalid.");
            }

            try
            {
                _ = Convert.FromHexString(checksum.Value);
            }
            catch (FormatException exception)
            {
                throw new ReplicaSnapshotException("The snapshot checksum index is invalid.", exception);
            }

            index.Add(entryPath, checksum);
        }

        return index;
    }

    private static void ValidateInventory(
        IReadOnlyList<ReplicaApplication> applications,
        ReplicaEnvironmentInventory environment,
        ReplicaWindowsInfo windows,
        IReadOnlyList<ReplicaFontInfo> fonts,
        IReadOnlyList<ReplicaPluginSnapshot> plugins)
    {
        if (applications is null ||
            environment is null ||
            environment.Variables is null ||
            environment.PathEntries is null ||
            windows is null ||
            windows.Capabilities is null ||
            fonts is null ||
            plugins is null ||
            applications.Any(application =>
                application is null ||
                application.PackageIdentity is null ||
                application.Artifacts is null ||
                string.IsNullOrWhiteSpace(application.DisplayName)) ||
            environment.Variables.Any(variable =>
                variable is null || string.IsNullOrWhiteSpace(variable.Name)) ||
            environment.PathEntries.Any(pathEntry => pathEntry is null || pathEntry.Order < 0) ||
            fonts.Any(font => font is null) ||
            plugins.Any(plugin =>
                plugin is null ||
                string.IsNullOrWhiteSpace(plugin.PluginId) ||
                string.IsNullOrWhiteSpace(plugin.PluginVersion) ||
                plugin.Capabilities is null ||
                plugin.Artifacts is null ||
                plugin.Values is null && plugin.Files is not null ||
                plugin.Values?.Keys.Any(string.IsNullOrWhiteSpace) == true ||
                plugin.Files?.Where(file => file is not null)
                    .Select(file => file.LogicalPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() != plugin.Files?.Count ||
                plugin.Files?.Any(file =>
                    file is null ||
                    string.IsNullOrWhiteSpace(file.LogicalPath) ||
                    Path.IsPathRooted(file.LogicalPath) ||
                    file.LogicalPath.Split('/', '\\').Contains("..", StringComparer.Ordinal)) == true) ||
            plugins.Select(plugin => plugin.PluginId).Distinct(
                StringComparer.Ordinal).Count() != plugins.Count)
        {
            throw new ReplicaSnapshotException("Snapshot inventory metadata is invalid.");
        }
    }

    private static void ValidateSnapshotSemantics(
        ReplicaSnapshotManifest manifest,
        ReplicaWindowsInfo windows,
        IReadOnlyList<ReplicaExclusion> exclusions,
        ReplicaRecoveryOptions recovery,
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        IReadOnlyDictionary<string, ReplicaChecksum> checksums)
    {
        if (recovery.SelectedFolders is null ||
            recovery.OfflineInstallers is null ||
            recovery.SelectedFolders.Any(folder => folder is null || !Enum.IsDefined(folder.Category)) ||
            recovery.OfflineInstallers.Any(installer =>
                installer is null || string.IsNullOrWhiteSpace(installer.Provenance)) ||
            !manifest.Exclusions.SequenceEqual(exclusions) ||
            !WindowsInfoMatches(windows, manifest.Metadata.Machine.Windows))
        {
            throw new ReplicaSnapshotException("Snapshot exclusion metadata is inconsistent.");
        }

        IReadOnlyList<string> payloadEntries = entries.Keys
            .Where(path => path.StartsWith("files/", StringComparison.Ordinal))
            .ToArray();

        if (manifest.SnapshotType == SnapshotType.Lightweight &&
            (payloadEntries.Count > 0 || recovery.SelectedFolders.Count > 0 || recovery.OfflineInstallers.Count > 0))
        {
            throw new ReplicaSnapshotException("A Lightweight snapshot contains recovery payloads.");
        }

        if (manifest.SnapshotType == SnapshotType.Recovery && recovery.OfflineInstallers.Count > 0)
        {
            throw new ReplicaSnapshotException("A Recovery snapshot contains offline installers.");
        }

        HashSet<string> declaredArtifacts = manifest.Metadata.Artifacts
            .Select(artifact => SnapshotPathValidator.NormalizeEntryPath(artifact.ArchivePath, 32))
            .ToHashSet(StringComparer.Ordinal);
        if (!declaredArtifacts.SetEquals(payloadEntries))
        {
            throw new ReplicaSnapshotException("Snapshot payload declarations do not match the archive.");
        }

        foreach (ReplicaArtifact artifact in manifest.Metadata.Artifacts)
        {
            if (artifact is null ||
                artifact.Size < 0 ||
                artifact.Sha256.Length != 64 ||
                !checksums.TryGetValue(artifact.ArchivePath, out ReplicaChecksum? checksum) ||
                !string.Equals(artifact.Sha256, checksum.Value, StringComparison.OrdinalIgnoreCase))
            {
                throw new ReplicaSnapshotException("Snapshot artifact metadata is invalid.");
            }
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

    private async Task<T> ReadJsonEntryAsync<T>(
        ZipArchiveEntry entry,
        ReplicaSnapshotManifest manifest,
        byte[]? encryptionKey,
        CancellationToken cancellationToken)
    {
        byte[] bytes = await ReadPlaintextEntryBytesAsync(
            entry,
            manifest,
            encryptionKey,
            limits.MaximumJsonSize,
            cancellationToken).ConfigureAwait(false);
        return DeserializeJson<T>(bytes);
    }

    private static T DeserializeJson<T>(byte[] bytes)
    {
        try
        {
            return SnapshotJson.Deserialize<T>(bytes);
        }
        catch (JsonException exception)
        {
            throw new ReplicaSnapshotException("The snapshot contains invalid JSON metadata.", exception);
        }
    }

    private static async Task<byte[]> ReadRawEntryBytesAsync(
        ZipArchiveEntry entry,
        int maximumSize,
        CancellationToken cancellationToken)
    {
        if (entry.Length > maximumSize)
        {
            throw new ReplicaSnapshotException("A snapshot metadata entry exceeds the configured size limit.");
        }

        await using Stream source = entry.Open();
        using MemoryStream destination = new(capacity: checked((int)entry.Length));
        await source.CopyToAsync(destination, 128 * 1024, cancellationToken).ConfigureAwait(false);
        return destination.ToArray();
    }

    private static async Task<byte[]> ReadPlaintextEntryBytesAsync(
        ZipArchiveEntry entry,
        ReplicaSnapshotManifest manifest,
        byte[]? encryptionKey,
        int maximumSize,
        CancellationToken cancellationToken)
    {
        using MemoryStream destination = new();
        if (manifest.Encryption is null && entry.Length > maximumSize)
        {
            throw new ReplicaSnapshotException("A snapshot metadata entry exceeds the configured size limit.");
        }

        await CopyPlaintextEntryAsync(
            entry,
            destination,
            manifest,
            encryptionKey,
            maximumSize,
            cancellationToken).ConfigureAwait(false);
        if (destination.Length > maximumSize)
        {
            throw new ReplicaSnapshotException("A snapshot metadata entry exceeds the configured size limit.");
        }

        return destination.ToArray();
    }

    internal static async Task CopyPlaintextEntryAsync(
        ZipArchiveEntry entry,
        Stream destination,
        ReplicaSnapshotManifest manifest,
        byte[]? encryptionKey,
        long maximumSize,
        CancellationToken cancellationToken)
    {
        await using Stream source = entry.Open();
        if (manifest.Encryption is not null &&
            !string.Equals(entry.FullName, SnapshotEntryNames.Manifest, StringComparison.Ordinal))
        {
            if (encryptionKey is null)
            {
                throw new ReplicaSnapshotDecryptionException();
            }

            await SnapshotCrypto.DecryptAsync(
                source,
                destination,
                encryptionKey,
                manifest,
                entry.FullName,
                maximumSize,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await source.CopyToAsync(destination, 128 * 1024, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class HashingWriteStream : Stream
    {
        private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private bool hashFinalized;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public string GetHash()
        {
            if (hashFinalized)
            {
                throw new InvalidOperationException("The hash has already been finalized.");
            }

            hashFinalized = true;
            return Convert.ToHexString(hash.GetHashAndReset());
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            hash.AppendData(buffer, offset, count);
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer.Span);
            return ValueTask.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                hash.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
