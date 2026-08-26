using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Replica.Core.Services;
using Replica.Core.Snapshots;

namespace Replica.Infrastructure.Snapshots;

public sealed class ReplicaSnapshotReader : ISnapshotReader
{
    private const int EndOfCentralDirectoryMinimumSize = 22;
    private const int MaximumZipCommentSize = ushort.MaxValue;
    private const long MaximumCentralDirectorySize = 64L * 1024 * 1024;
    private const long MaximumArchiveOverhead = 128L * 1024 * 1024;
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
            ValidateZipStructurePreflight(archiveStream);
            archiveStream.Position = 0;
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

    private void ValidateZipStructurePreflight(FileStream archiveStream)
    {
        long maximumPhysicalSize = checked(
            limits.MaximumTotalUncompressedSize + MaximumArchiveOverhead);
        if (archiveStream.Length < EndOfCentralDirectoryMinimumSize ||
            archiveStream.Length > maximumPhysicalSize)
        {
            throw new ReplicaSnapshotException(
                "The snapshot archive size exceeds the configured limit.");
        }

        int tailLength = (int)Math.Min(
            archiveStream.Length,
            EndOfCentralDirectoryMinimumSize + MaximumZipCommentSize);
        byte[] tail = new byte[tailLength];
        archiveStream.Position = archiveStream.Length - tailLength;
        archiveStream.ReadExactly(tail);

        int endRecordOffset = FindEndOfCentralDirectory(tail);
        if (endRecordOffset < 0)
        {
            throw new ReplicaSnapshotException(
                "The snapshot ZIP end record is missing or malformed.");
        }

        ReadOnlySpan<byte> endRecord = tail.AsSpan(endRecordOffset);
        ushort diskNumber = BinaryPrimitives.ReadUInt16LittleEndian(endRecord[4..]);
        ushort centralDirectoryDisk = BinaryPrimitives.ReadUInt16LittleEndian(endRecord[6..]);
        ulong entriesOnDisk = BinaryPrimitives.ReadUInt16LittleEndian(endRecord[8..]);
        ulong totalEntries = BinaryPrimitives.ReadUInt16LittleEndian(endRecord[10..]);
        ulong centralDirectorySize = BinaryPrimitives.ReadUInt32LittleEndian(endRecord[12..]);
        ulong centralDirectoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(endRecord[16..]);
        bool requiresZip64 = entriesOnDisk == ushort.MaxValue ||
            totalEntries == ushort.MaxValue ||
            centralDirectorySize == uint.MaxValue ||
            centralDirectoryOffset == uint.MaxValue;

        long absoluteEndRecordOffset = archiveStream.Length - tailLength + endRecordOffset;
        if (requiresZip64)
        {
            (diskNumber,
                centralDirectoryDisk,
                entriesOnDisk,
                totalEntries,
                centralDirectorySize,
                centralDirectoryOffset) = ReadZip64DirectoryFacts(
                archiveStream,
                absoluteEndRecordOffset);
        }

        if (diskNumber != 0 ||
            centralDirectoryDisk != 0 ||
            entriesOnDisk != totalEntries ||
            totalEntries == 0 ||
            totalEntries > (ulong)limits.MaximumEntryCount ||
            centralDirectorySize == 0 ||
            centralDirectorySize > MaximumCentralDirectorySize ||
            centralDirectoryOffset > (ulong)archiveStream.Length ||
            centralDirectorySize > (ulong)archiveStream.Length - centralDirectoryOffset ||
            centralDirectoryOffset + centralDirectorySize > (ulong)absoluteEndRecordOffset)
        {
            throw new ReplicaSnapshotException(
                "The snapshot ZIP central directory exceeds the configured limits.");
        }
    }

    private static int FindEndOfCentralDirectory(ReadOnlySpan<byte> tail)
    {
        const uint signature = 0x06054B50;
        for (int offset = tail.Length - EndOfCentralDirectoryMinimumSize; offset >= 0; offset--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail[offset..]) != signature)
            {
                continue;
            }

            ushort commentLength = BinaryPrimitives.ReadUInt16LittleEndian(tail[(offset + 20)..]);
            if (offset + EndOfCentralDirectoryMinimumSize + commentLength == tail.Length)
            {
                return offset;
            }
        }

        return -1;
    }

    private static (ushort DiskNumber,
        ushort CentralDirectoryDisk,
        ulong EntriesOnDisk,
        ulong TotalEntries,
        ulong CentralDirectorySize,
        ulong CentralDirectoryOffset) ReadZip64DirectoryFacts(
        FileStream archiveStream,
        long absoluteEndRecordOffset)
    {
        const uint locatorSignature = 0x07064B50;
        const uint endRecordSignature = 0x06064B50;
        const int locatorSize = 20;
        const int minimumEndRecordSize = 56;
        if (absoluteEndRecordOffset < locatorSize)
        {
            throw new ReplicaSnapshotException("The snapshot ZIP64 locator is missing.");
        }

        Span<byte> locator = stackalloc byte[locatorSize];
        archiveStream.Position = absoluteEndRecordOffset - locatorSize;
        archiveStream.ReadExactly(locator);
        if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != locatorSignature ||
            BinaryPrimitives.ReadUInt32LittleEndian(locator[4..]) != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(locator[16..]) != 1)
        {
            throw new ReplicaSnapshotException("The snapshot ZIP64 locator is invalid.");
        }

        ulong zip64RecordOffset = BinaryPrimitives.ReadUInt64LittleEndian(locator[8..]);
        if (zip64RecordOffset > (ulong)archiveStream.Length - minimumEndRecordSize)
        {
            throw new ReplicaSnapshotException("The snapshot ZIP64 end record is invalid.");
        }

        Span<byte> endRecord = stackalloc byte[minimumEndRecordSize];
        archiveStream.Position = (long)zip64RecordOffset;
        archiveStream.ReadExactly(endRecord);
        if (BinaryPrimitives.ReadUInt32LittleEndian(endRecord) != endRecordSignature ||
            BinaryPrimitives.ReadUInt64LittleEndian(endRecord[4..]) < 44)
        {
            throw new ReplicaSnapshotException("The snapshot ZIP64 end record is invalid.");
        }

        uint diskNumber = BinaryPrimitives.ReadUInt32LittleEndian(endRecord[16..]);
        uint centralDirectoryDisk = BinaryPrimitives.ReadUInt32LittleEndian(endRecord[20..]);
        if (diskNumber > ushort.MaxValue || centralDirectoryDisk > ushort.MaxValue)
        {
            throw new ReplicaSnapshotException("Multi-disk snapshots are not supported.");
        }

        return (
            (ushort)diskNumber,
            (ushort)centralDirectoryDisk,
            BinaryPrimitives.ReadUInt64LittleEndian(endRecord[24..]),
            BinaryPrimitives.ReadUInt64LittleEndian(endRecord[32..]),
            BinaryPrimitives.ReadUInt64LittleEndian(endRecord[40..]),
            BinaryPrimitives.ReadUInt64LittleEndian(endRecord[48..]));
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
        bool isCurrentSchema = string.Equals(
            manifest.SchemaVersion,
            ReplicaSnapshotManifest.CurrentSchemaVersion,
            StringComparison.Ordinal);
        bool isLegacySchema = string.Equals(
            manifest.SchemaVersion,
            ReplicaSnapshotManifest.LegacySchemaVersion,
            StringComparison.Ordinal);
        bool platformMetadataMatches = PlatformMetadataMatches(manifest);
        bool isSupportedLegacyWindows = isLegacySchema &&
            manifest.SourcePlatform == ReplicaPlatformFamily.Windows &&
            (manifest.Metadata?.Machine?.Platform is null || platformMetadataMatches);
        bool isSupportedLegacyMac = isLegacySchema &&
            manifest.SourcePlatform == ReplicaPlatformFamily.MacOS &&
            platformMetadataMatches;

        if ((!isCurrentSchema && !isSupportedLegacyWindows && !isSupportedLegacyMac) ||
            isCurrentSchema && !platformMetadataMatches ||
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
            manifest.Metadata.Artifacts
                .Where(artifact => artifact is not null)
                .Select(artifact => artifact.ArchivePath)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Metadata.Artifacts.Count ||
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
                StringComparison.Ordinal))
        {
            throw new ReplicaSnapshotException("The snapshot manifest is invalid or unsupported.");
        }
    }

    private static bool PlatformMetadataMatches(ReplicaSnapshotManifest manifest)
    {
        ReplicaMachineInfo? machine = manifest.Metadata?.Machine;
        ReplicaPlatformInfo? platform = machine?.Platform;
        ReplicaWindowsInfo? windows = machine?.Windows;
        return platform is not null && windows is not null &&
            platform.Family == manifest.SourcePlatform &&
            string.Equals(platform.DisplayName, windows.Edition, StringComparison.Ordinal) &&
            string.Equals(platform.Version, windows.Version, StringComparison.Ordinal) &&
            string.Equals(platform.Build, windows.Build, StringComparison.Ordinal) &&
            string.Equals(platform.Architecture, machine!.Architecture, StringComparison.Ordinal) &&
            string.Equals(platform.Architecture, windows.Architecture, StringComparison.Ordinal) &&
            string.Equals(platform.Locale, machine.Locale, StringComparison.Ordinal) &&
            string.Equals(platform.Locale, windows.Locale, StringComparison.Ordinal) &&
            string.Equals(platform.TimeZone, windows.TimeZone, StringComparison.Ordinal) &&
            platform.Capabilities is not null && windows.Capabilities is not null &&
            platform.Capabilities.SequenceEqual(windows.Capabilities, StringComparer.Ordinal);
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
                variable is null ||
                string.IsNullOrWhiteSpace(variable.Name) ||
                SensitiveEnvironmentPolicy.IsSensitiveName(variable.Name)) ||
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
            recovery.OfflineInstallers.Count > 100 ||
            recovery.SelectedFolders.Any(folder => folder is null || !Enum.IsDefined(folder.Category)) ||
            recovery.OfflineInstallers.Any(installer =>
                installer is null ||
                string.IsNullOrWhiteSpace(installer.Provenance) ||
                !HasValidOptionalInstallerIdentity(installer)) ||
            recovery.OfflineInstallers
                .Where(installer => installer is not null)
                .Select(installer => installer.ArchivePath)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != recovery.OfflineInstallers.Count ||
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

        foreach (ReplicaOfflineInstaller installer in recovery.OfflineInstallers)
        {
            if (installer.ExpectedSha256 is null)
            {
                continue;
            }

            string archivePath = SnapshotPathValidator.BuildInstallerEntryPath(installer.ArchivePath);
            ReplicaArtifact? artifact = manifest.Metadata.Artifacts.SingleOrDefault(candidate =>
                string.Equals(candidate.ArchivePath, archivePath, StringComparison.Ordinal));
            if (artifact is null ||
                installer.ExpectedSize != artifact.Size ||
                !string.Equals(installer.ExpectedSha256, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new ReplicaSnapshotException(
                    "Offline installer review metadata does not match the archived payload.");
            }
        }
    }

    private static bool HasValidOptionalInstallerIdentity(ReplicaOfflineInstaller installer)
    {
        bool hasAnyIdentity = installer.Publisher is not null ||
            installer.PublisherCertificateSha256 is not null ||
            installer.ExpectedSha256 is not null ||
            installer.ExpectedSize is not null;
        if (!hasAnyIdentity)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(installer.Publisher) &&
            IsSha256(installer.PublisherCertificateSha256) &&
            IsSha256(installer.ExpectedSha256) &&
            installer.ExpectedSize is > 0 and <= 2L * 1024 * 1024 * 1024;
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
