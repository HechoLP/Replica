using System.IO.Compression;
using System.Security.Cryptography;
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
    }

    public async Task<MaterializedRecoveryPayload> MaterializeAsync(
        string sessionId,
        string snapshotPath,
        ReplicaSnapshotReadResult snapshot,
        IReadOnlyList<RecoveryPathMapping> mappings,
        ReadOnlyMemory<char> password,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(sessionId, "N", out _))
        {
            throw new ArgumentException("The recovery session identifier is invalid.", nameof(sessionId));
        }

        string payloadRoot = GetPayloadRoot(sessionId);
        Directory.CreateDirectory(payloadRoot);
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
        finally
        {
            if (key is not null)
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }

        return new MaterializedRecoveryPayload(payloadRoot, files);
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

    private string GetPayloadRoot(string sessionId)
    {
        string recoveryRoot = Path.GetFullPath(_pathProvider.RecoveryDirectory);
        string payload = Path.GetFullPath(Path.Combine(recoveryRoot, "Sessions", sessionId, "Payload"));
        string prefix = recoveryRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!payload.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Recovery payload path escaped its root.");
        }

        return payload;
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
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal sealed record MaterializedRecoveryPayload(
    string RootPath,
    IReadOnlyDictionary<string, MaterializedRecoveryFile> Files);

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
