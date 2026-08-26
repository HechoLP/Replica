using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Replica.Core.Execution;
using Replica.Core.Recovery;
using Replica.Core.Snapshots;
using Replica.Infrastructure.Snapshots;

namespace Replica.Infrastructure.Recovery;

internal sealed class RecoveryPayloadMaterializer
{
    private readonly ReplicaSnapshotReadLimits _limits = ReplicaSnapshotReadLimits.Default;
    private readonly Replica.Core.Services.IReplicaPathProvider _pathProvider;

    public RecoveryPayloadMaterializer(Replica.Core.Services.IReplicaPathProvider pathProvider)
    {
        _pathProvider = pathProvider;
        CleanupAbandonedPayloads();
    }

    public async Task<MaterializedRecoveryPayload> MaterializeAsync(
        string sessionId,
        string snapshotPath,
        ReplicaSnapshotReadResult snapshot,
        string expectedSnapshotSha256,
        IReadOnlyList<RecoveryPathMapping> mappings,
        ReadOnlyMemory<char> password,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(sessionId, "N", out _))
        {
            throw new ArgumentException("The recovery session identifier is invalid.", nameof(sessionId));
        }

        string payloadsRoot = GetPayloadsRoot(sessionId);
        EnsureRestrictedDirectory(payloadsRoot);
        CleanupAbandonedPayloads();
        string payloadRoot = GetContainedPath(payloadsRoot, $"payload-{Guid.NewGuid():N}");
        EnsureRestrictedDirectory(payloadRoot);
        RejectReparsePoint(payloadRoot);
        Dictionary<string, ReplicaChecksum> checksums = snapshot.Checksums.ToDictionary(
            checksum => checksum.EntryPath,
            StringComparer.Ordinal);
        Dictionary<string, ReplicaArtifact> artifacts = snapshot.Manifest.Metadata.Artifacts.ToDictionary(
            artifact => artifact.ArchivePath,
            StringComparer.Ordinal);
        Dictionary<string, MaterializedRecoveryFile> files = new(StringComparer.Ordinal);
        byte[]? key = null;

        try
        {
            EnsureStagingCapacity(payloadRoot, snapshot, mappings);
            if (snapshot.Manifest.Encryption is not null)
            {
                key = SnapshotCrypto.VerifyManifestAndDeriveKey(snapshot.Manifest, password);
            }

            await using FileStream archiveStream = new(
                snapshotPath,
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
                    "The recovery snapshot changed before payload extraction.");
            }

            archiveStream.Position = 0;
            using ZipArchive archive = new(archiveStream, ZipArchiveMode.Read, leaveOpen: false);
            foreach (ZipArchiveEntry entry in archive.Entries
                         .Where(entry => entry.FullName.StartsWith("files/", StringComparison.Ordinal))
                         .OrderBy(entry => entry.FullName, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string entryPath = SnapshotPathValidator.NormalizeEntryPath(entry.FullName, 32);
                SnapshotPathValidator.RejectLinkLikeEntry(entry);
                if (!snapshot.EntryPaths.Contains(entryPath, StringComparer.Ordinal) ||
                    !checksums.TryGetValue(entryPath, out ReplicaChecksum? checksum) ||
                    !artifacts.TryGetValue(entryPath, out ReplicaArtifact? artifact))
                {
                    throw new ReplicaSnapshotException("A recovery payload entry is not declared.");
                }

                MappedDestination? destination = ResolveDestination(
                    entryPath,
                    snapshot.Recovery.SelectedFolders,
                    mappings);
                if (destination is null)
                {
                    continue;
                }

                string relative = entryPath.Replace('/', Path.DirectorySeparatorChar);
                string materializedPath = GetContainedPath(payloadRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(materializedPath)!);
                RejectReparsePoint(Path.GetDirectoryName(materializedPath)!);
                string temporary = $"{materializedPath}.{Guid.NewGuid():N}.tmp";
                try
                {
                    await using (FileStream output = new(
                                     temporary,
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
                            key,
                            Math.Min(_limits.MaximumEntrySize, Math.Max(artifact.Size, 0)),
                            cancellationToken).ConfigureAwait(false);
                        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                        output.Flush(flushToDisk: true);
                    }

                    string actualHash = await HashFileAsync(temporary, cancellationToken)
                        .ConfigureAwait(false);
                    if (!FixedHashEquals(actualHash, checksum.Value))
                    {
                        throw new ReplicaSnapshotException(
                            "A materialized recovery payload failed checksum validation.");
                    }

                    File.Move(temporary, materializedPath, overwrite: true);
                }
                finally
                {
                    TryDelete(temporary);
                }

                files.Add(entryPath, new MaterializedRecoveryFile(
                    entryPath,
                    relative,
                    destination.TargetPath,
                    destination.ApprovedRoot,
                    checksum.Value,
                    artifact.Size,
                    destination.ConflictBehavior));
            }
        }
        catch (Exception exception)
        {
            if (!DeletePayloadRoot(payloadRoot) && exception is not OutOfMemoryException)
            {
                throw new IOException(
                    "Recovery payload extraction failed and plaintext cleanup is still pending.",
                    exception);
            }

            throw;
        }
        finally
        {
            if (key is not null)
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }

        return new MaterializedRecoveryPayload(payloadRoot, files, DeletePayloadRoot);
    }

    public Task<bool> CleanupSessionPayloadsAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(sessionId, "N", out _))
        {
            throw new ArgumentException("The recovery session identifier is invalid.", nameof(sessionId));
        }

        cancellationToken.ThrowIfCancellationRequested();
        string sessionRoot;
        try
        {
            sessionRoot = GetSessionRoot(sessionId);
        }
        catch (InvalidDataException)
        {
            return Task.FromResult(false);
        }

        bool legacyDeleted = DeletePayloadRoot(Path.Combine(sessionRoot, "Payload"));
        bool leasedDeleted = DeletePayloadRoot(Path.Combine(sessionRoot, "Payloads"));
        return Task.FromResult(legacyDeleted && leasedDeleted);
    }

    internal static MappedDestination? ResolveDestination(
        string entryPath,
        IReadOnlyList<ReplicaSelectedFolder> selectedFolders,
        IReadOnlyList<RecoveryPathMapping> mappings)
    {
        foreach (ReplicaSelectedFolder folder in selectedFolders)
        {
            string archiveRoot = folder.ArchivePath.Replace('\\', '/').Trim('/');
            string prefix = $"files/{archiveRoot}/";
            if (!entryPath.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            RecoveryPathMapping? mapping = mappings.FirstOrDefault(candidate =>
                Path.GetFullPath(candidate.SourcePath).Equals(
                    Path.GetFullPath(folder.SourcePath),
                    StringComparison.OrdinalIgnoreCase));
            if (mapping is null)
            {
                return null;
            }

            string targetRoot = Path.GetFullPath(mapping.TargetPath);
            string relative = entryPath[prefix.Length..].Replace('/', Path.DirectorySeparatorChar);
            string target = Path.GetFullPath(Path.Combine(targetRoot, relative));
            string rootPrefix = targetRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!target.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new ReplicaSnapshotException("A mapped recovery path escaped its root.");
            }

            return new MappedDestination(
                target,
                targetRoot,
                mapping.ConflictBehavior);
        }

        return null;
    }

    private string GetSessionRoot(string sessionId)
    {
        string recoveryRoot = Path.GetFullPath(_pathProvider.RecoveryDirectory);
        string session = Path.GetFullPath(Path.Combine(recoveryRoot, "Sessions", sessionId));
        string prefix = recoveryRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!session.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Recovery payload path escaped its root.");
        }

        if (Directory.Exists(session) && SnapshotPathValidator.ContainsReparsePoint(session))
        {
            throw new InvalidDataException("Recovery payload session path contains a reparse point.");
        }

        return session;
    }

    private string GetPayloadsRoot(string sessionId)
    {
        return Path.GetFullPath(Path.Combine(GetSessionRoot(sessionId), "Payloads"));
    }

    private static string GetContainedPath(string root, string relative)
    {
        string full = Path.GetFullPath(Path.Combine(root, relative));
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ReplicaSnapshotException("Recovery payload path escaped its root.");
        }

        return full;
    }

    private static void RejectReparsePoint(string path)
    {
        if (SnapshotPathValidator.ContainsReparsePoint(path))
        {
            throw new ReplicaSnapshotException("Recovery payload path contains a reparse point.");
        }
    }

    private static void EnsureStagingCapacity(
        string payloadRoot,
        ReplicaSnapshotReadResult snapshot,
        IReadOnlyList<RecoveryPathMapping> mappings)
    {
        long required = snapshot.Manifest.Metadata.Artifacts
            .Where(artifact => artifact.ArchivePath.StartsWith("files/", StringComparison.Ordinal))
            .Where(artifact => ResolveDestination(
                artifact.ArchivePath,
                snapshot.Recovery.SelectedFolders,
                mappings) is not null)
            .Aggregate(0L, (total, artifact) => checked(total + Math.Max(artifact.Size, 0)));
        string root = Path.GetPathRoot(payloadRoot) ??
            throw new IOException("Recovery staging volume is unavailable.");
        long available = new DriveInfo(root).AvailableFreeSpace;
        const long reserve = 64L * 1024 * 1024;
        if (required > Math.Max(0, available - reserve))
        {
            throw new IOException("Recovery staging does not have enough free space.");
        }
    }

    private static void EnsureRestrictedDirectory(string path)
    {
        Directory.CreateDirectory(path);
        DirectoryInfo directory = new(path);
        if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("Recovery payload directory is unsafe.");
        }

        SecurityIdentifier user = WindowsIdentity.GetCurrent().User ??
            throw new InvalidOperationException("The current Windows identity is unavailable.");
        DirectorySecurity security = new();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddDirectoryRule(security, user);
        AddDirectoryRule(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        AddDirectoryRule(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        directory.SetAccessControl(security);
    }

    private static void AddDirectoryRule(DirectorySecurity security, SecurityIdentifier identity)
    {
        security.AddAccessRule(new FileSystemAccessRule(
            identity,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
    }

    private void CleanupAbandonedPayloads()
    {
        try
        {
            string sessionsRoot = Path.Combine(
                Path.GetFullPath(_pathProvider.RecoveryDirectory),
                "Sessions");
            if (!Directory.Exists(sessionsRoot) ||
                SnapshotPathValidator.ContainsReparsePoint(sessionsRoot))
            {
                return;
            }

            DateTime cutoff = DateTime.UtcNow.Subtract(TimeSpan.FromHours(24));
            List<string> candidates = [];
            foreach (string session in Directory.EnumerateDirectories(sessionsRoot).Take(128))
            {
                if (new DirectoryInfo(session).Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                    SnapshotPathValidator.ContainsReparsePoint(session))
                {
                    continue;
                }

                string legacy = Path.Combine(session, "Payload");
                if (Directory.Exists(legacy))
                {
                    candidates.Add(legacy);
                }

                string payloads = Path.Combine(session, "Payloads");
                if (Directory.Exists(payloads) &&
                    !new DirectoryInfo(payloads).Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    candidates.AddRange(Directory.EnumerateDirectories(payloads).Take(32));
                }
            }

            foreach (string directory in candidates
                         .Where(directory => Directory.GetLastWriteTimeUtc(directory) < cutoff)
                         .OrderBy(Directory.GetLastWriteTimeUtc)
                         .Take(32))
            {
                DeletePayloadRoot(directory);
            }
        }
        catch (IOException)
        {
            // Explicit session cleanup reports failures; startup sweeping remains best effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Explicit session cleanup reports failures; startup sweeping remains best effort.
        }
    }

    private static bool DeletePayloadRoot(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return true;
            }

            DirectoryInfo root = new(path);
            if (root.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                Directory.Delete(path, recursive: false);
                return !Directory.Exists(path);
            }

            DeleteDirectoryNoFollow(root);
            return !Directory.Exists(path);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void DeleteDirectoryNoFollow(DirectoryInfo directory)
    {
        foreach (FileInfo file in directory.EnumerateFiles())
        {
            file.Delete();
        }

        foreach (DirectoryInfo child in directory.EnumerateDirectories())
        {
            if (child.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                child.Delete(recursive: false);
            }
            else
            {
                DeleteDirectoryNoFollow(child);
            }
        }

        directory.Delete(recursive: false);
    }

    private static async Task<string> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)
            .ConfigureAwait(false));
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

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort cleanup; the caller removes the bounded materialization root later.
        }
        catch (UnauthorizedAccessException)
        {
            // Cleanup failure does not make the materialized payload eligible for restore.
        }
    }
}

internal sealed class MaterializedRecoveryPayload : IDisposable
{
    private readonly Func<string, bool> _cleanup;
    private int _disposed;

    public MaterializedRecoveryPayload(
        string rootPath,
        IReadOnlyDictionary<string, MaterializedRecoveryFile> files,
        Func<string, bool> cleanup)
    {
        RootPath = rootPath;
        Files = files;
        _cleanup = cleanup;
    }

    public string RootPath { get; }

    public IReadOnlyDictionary<string, MaterializedRecoveryFile> Files { get; }

    public bool? CleanupSucceeded { get; private set; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            CleanupSucceeded = _cleanup(RootPath);
        }
    }
}

internal sealed record MaterializedRecoveryFile(
    string EntryPath,
    string MaterializedRelativePath,
    string DestinationPath,
    string ApprovedRoot,
    string Sha256,
    long Size,
    RestoreFileConflictBehavior ConflictBehavior);

internal sealed record MappedDestination(
    string TargetPath,
    string ApprovedRoot,
    RestoreFileConflictBehavior ConflictBehavior);
