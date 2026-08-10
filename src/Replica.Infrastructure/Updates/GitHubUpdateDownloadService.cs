using System.Security.Cryptography;
using System.Text;
using Replica.Core.Portable;
using Replica.Core.Services;
using Replica.Core.Updates;
using Replica.Infrastructure.Portable;

namespace Replica.Infrastructure.Updates;

public sealed class GitHubUpdateDownloadService : IUpdateDownloadService
{
    private const int BufferSize = 128 * 1024;
    private readonly IFileHashService hashes;
    private readonly UpdateDownloadOptions options;
    private readonly IReplicaPathProvider paths;
    private readonly IOfficialReleaseSource releaseSource;
    private readonly TimeProvider timeProvider;

    public GitHubUpdateDownloadService(
        IOfficialReleaseSource releaseSource,
        IReplicaPathProvider paths,
        IFileHashService hashes,
        UpdateDownloadOptions options,
        TimeProvider timeProvider)
    {
        this.releaseSource = releaseSource;
        this.paths = paths;
        this.hashes = hashes;
        this.options = options;
        this.timeProvider = timeProvider;
    }

    public async Task<UpdateDownloadResult> DownloadAsync(
        UpdateDownloadRequest request,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.UserApproved)
        {
            throw new UpdateDownloadException(
                UpdateDownloadErrorCode.ApprovalRequired,
                "Update download requires explicit user approval.");
        }

        GitHubReleaseDetails release = request.Release;
        if (release.IsDraft)
        {
            throw new UpdateDownloadException(
                UpdateDownloadErrorCode.InvalidAsset,
                "Draft releases cannot be downloaded.");
        }

        GitHubReleaseAssetInfo[] installers = release.Assets.Where(asset =>
                asset.Name.Equals("ReplicaSetup.exe", StringComparison.Ordinal))
            .ToArray();
        if (installers.Length != 1)
        {
            throw new UpdateDownloadException(
                UpdateDownloadErrorCode.MissingAsset,
                "The release must contain exactly one ReplicaSetup.exe asset.");
        }

        GitHubReleaseAssetInfo installer = installers[0];
        ValidateAsset(release, installer, requireInstallerName: true);
        if (installer.Size is <= 0 || installer.Size > options.MaximumInstallerSize)
        {
            throw new UpdateDownloadException(
                UpdateDownloadErrorCode.TooLarge,
                "ReplicaSetup.exe exceeds the update download size limit.");
        }

        string updateRoot = Path.Combine(paths.TemporaryDirectory, "Updates");
        string sessionDirectory = Path.Combine(updateRoot, Guid.NewGuid().ToString("N"));
        EnsureSafeDirectoryRoot(updateRoot);
        Directory.CreateDirectory(sessionDirectory);
        string temporaryPath = Path.Combine(sessionDirectory, ".ReplicaSetup.download");
        string finalPath = Path.Combine(sessionDirectory, "ReplicaSetup.exe");
        bool completed = false;

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        CancellationToken operationToken = timeout.Token;
        try
        {
            progress?.Report(new UpdateDownloadProgress(UpdateDownloadStage.Preparing, 0, installer.Size));
            string? expectedHash = installer.Sha256;
            GitHubReleaseAssetInfo? checksumAsset = SelectChecksumAsset(release.Assets);
            if (checksumAsset is not null)
            {
                ValidateAsset(release, checksumAsset, requireInstallerName: false);
                progress?.Report(new UpdateDownloadProgress(
                    UpdateDownloadStage.DownloadingChecksum,
                    0,
                    checksumAsset.Size));
                string checksumHash = await ReadChecksumAsync(checksumAsset, operationToken)
                    .ConfigureAwait(false);
                if (expectedHash is not null &&
                    !expectedHash.Equals(checksumHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new UpdateDownloadException(
                        UpdateDownloadErrorCode.ChecksumInvalid,
                        "GitHub digest and checksum asset do not agree.");
                }

                expectedHash = checksumHash;
            }

            progress?.Report(new UpdateDownloadProgress(
                UpdateDownloadStage.DownloadingInstaller,
                0,
                installer.Size));
            string streamingHash = await DownloadInstallerAsync(
                installer,
                temporaryPath,
                progress,
                operationToken).ConfigureAwait(false);
            EnsureTemporaryFileSafe(temporaryPath, installer.Size);

            progress?.Report(new UpdateDownloadProgress(
                UpdateDownloadStage.Verifying,
                installer.Size,
                installer.Size));
            string fileHash = await hashes.ComputeSha256Async(temporaryPath, operationToken)
                .ConfigureAwait(false);
            if (!streamingHash.Equals(fileHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new UpdateDownloadException(
                    UpdateDownloadErrorCode.ChecksumMismatch,
                    "The downloaded installer changed during verification.");
            }

            if (expectedHash is not null &&
                !expectedHash.Equals(fileHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new UpdateDownloadException(
                    UpdateDownloadErrorCode.ChecksumMismatch,
                    "ReplicaSetup.exe failed SHA-256 verification.");
            }

            operationToken.ThrowIfCancellationRequested();
            EnsureSafeDirectoryRoot(updateRoot);
            EnsureTemporaryFileSafe(temporaryPath, installer.Size);
            File.Move(temporaryPath, finalPath, overwrite: false);
            completed = true;
            progress?.Report(new UpdateDownloadProgress(
                UpdateDownloadStage.Completed,
                installer.Size,
                installer.Size));
            return new UpdateDownloadResult(
                release,
                finalPath,
                installer.Size,
                fileHash,
                expectedHash is null ? UpdateChecksumStatus.Unavailable : UpdateChecksumStatus.Verified,
                timeProvider.GetUtcNow());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateDownloadException(
                UpdateDownloadErrorCode.TimedOut,
                "The update download timed out.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UpdateDownloadException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or UnauthorizedAccessException or
            ReplicaInstallerDownloadException or CryptographicException)
        {
            throw new UpdateDownloadException(
                UpdateDownloadErrorCode.IoFailure,
                "The update could not be downloaded safely.",
                exception);
        }
        finally
        {
            if (!completed)
            {
                DeleteSessionSafely(updateRoot, sessionDirectory);
            }
        }
    }

    private async Task<string> DownloadInstallerAsync(
        GitHubReleaseAssetInfo installer,
        string temporaryPath,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        OfficialReleaseAsset sourceAsset = ToOfficialAsset(installer);
        await using Stream source = await releaseSource.OpenAssetStreamAsync(sourceAsset, cancellationToken)
            .ConfigureAwait(false);
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
            if (total > installer.Size || total > options.MaximumInstallerSize)
            {
                throw new UpdateDownloadException(
                    UpdateDownloadErrorCode.TooLarge,
                    "The installer exceeded its declared size.");
            }

            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            progress?.Report(new UpdateDownloadProgress(
                UpdateDownloadStage.DownloadingInstaller,
                total,
                installer.Size));
        }

        if (total != installer.Size)
        {
            throw new UpdateDownloadException(
                UpdateDownloadErrorCode.InvalidAsset,
                "The installer size did not match its release metadata.");
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        destination.Flush(flushToDisk: true);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private async Task<string> ReadChecksumAsync(
        GitHubReleaseAssetInfo checksumAsset,
        CancellationToken cancellationToken)
    {
        if (checksumAsset.Size is <= 0 || checksumAsset.Size > options.MaximumChecksumSize)
        {
            throw new UpdateDownloadException(
                UpdateDownloadErrorCode.ChecksumInvalid,
                "The checksum asset exceeded its safety limit.");
        }

        await using Stream source = await releaseSource.OpenAssetStreamAsync(
            ToOfficialAsset(checksumAsset),
            cancellationToken).ConfigureAwait(false);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[4096];
        while (true)
        {
            int read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > options.MaximumChecksumSize)
            {
                throw new UpdateDownloadException(
                    UpdateDownloadErrorCode.ChecksumInvalid,
                    "The checksum asset exceeded its safety limit.");
            }

            buffer.Write(chunk, 0, read);
        }

        if (buffer.Length != checksumAsset.Size)
        {
            throw new UpdateDownloadException(
                UpdateDownloadErrorCode.ChecksumInvalid,
                "The checksum asset size did not match its release metadata.");
        }

        string text = Encoding.UTF8.GetString(buffer.ToArray());
        foreach (string line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 64 && trimmed.All(Uri.IsHexDigit))
            {
                return trimmed.ToUpperInvariant();
            }

            string[] parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 &&
                parts[0].Length == 64 &&
                parts[0].All(Uri.IsHexDigit) &&
                parts[^1].TrimStart('*').Equals("ReplicaSetup.exe", StringComparison.Ordinal))
            {
                return parts[0].ToUpperInvariant();
            }
        }

        throw new UpdateDownloadException(
            UpdateDownloadErrorCode.ChecksumInvalid,
            "The checksum asset did not contain a valid ReplicaSetup.exe SHA-256 value.");
    }

    private static GitHubReleaseAssetInfo? SelectChecksumAsset(
        IReadOnlyList<GitHubReleaseAssetInfo> assets)
    {
        GitHubReleaseAssetInfo[] exact = assets.Where(asset =>
                asset.Name.Equals("ReplicaSetup.exe.sha256", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (exact.Length > 1)
        {
            throw new UpdateDownloadException(
                UpdateDownloadErrorCode.ChecksumInvalid,
                "The release contains duplicate checksum assets.");
        }

        if (exact.Length == 1)
        {
            return exact[0];
        }

        GitHubReleaseAssetInfo[] sums = assets.Where(asset =>
                asset.Name.Equals("SHA256SUMS", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (sums.Length > 1)
        {
            throw new UpdateDownloadException(
                UpdateDownloadErrorCode.ChecksumInvalid,
                "The release contains duplicate checksum assets.");
        }

        return sums.SingleOrDefault();
    }

    private static void ValidateAsset(
        GitHubReleaseDetails release,
        GitHubReleaseAssetInfo asset,
        bool requireInstallerName)
    {
        string releasePrefix = $"/HechoLP/Replica/releases/download/{release.TagName}/";
        string uriFileName = Uri.UnescapeDataString(Path.GetFileName(asset.DownloadUri.AbsolutePath));
        if (!asset.DownloadUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !asset.DownloadUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !asset.DownloadUri.AbsolutePath.StartsWith(releasePrefix, StringComparison.Ordinal) ||
            !uriFileName.Equals(asset.Name, StringComparison.Ordinal) ||
            (requireInstallerName &&
             !asset.Name.Equals("ReplicaSetup.exe", StringComparison.Ordinal)))
        {
            throw new UpdateDownloadException(
                UpdateDownloadErrorCode.InvalidAsset,
                "The update asset is not an official Replica GitHub Release asset.");
        }
    }

    private static OfficialReleaseAsset ToOfficialAsset(GitHubReleaseAssetInfo asset) => new(
        asset.Name,
        asset.DownloadUri,
        asset.Size,
        asset.Sha256);

    private static void EnsureSafeDirectoryRoot(string updateRoot)
    {
        string fullPath = Path.GetFullPath(updateRoot);
        if (PortablePathSafety.ContainsReparsePoint(fullPath))
        {
            throw new UpdateDownloadException(
                UpdateDownloadErrorCode.IoFailure,
                "The update temporary directory is unsafe.");
        }
    }

    private static void EnsureTemporaryFileSafe(string temporaryPath, long expectedSize)
    {
        if (!File.Exists(temporaryPath))
        {
            throw new UpdateDownloadException(
                UpdateDownloadErrorCode.IoFailure,
                "The temporary installer disappeared.");
        }

        FileInfo file = new(temporaryPath);
        if (file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file.Length != expectedSize)
        {
            throw new UpdateDownloadException(
                UpdateDownloadErrorCode.InvalidAsset,
                "The temporary installer changed unexpectedly.");
        }
    }

    private static void DeleteSessionSafely(string updateRoot, string sessionDirectory)
    {
        try
        {
            string root = Path.GetFullPath(updateRoot);
            string session = Path.GetFullPath(sessionDirectory);
            string relative = Path.GetRelativePath(root, session);
            if (!Path.IsPathRooted(relative) &&
                relative != ".." &&
                !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                Directory.Exists(session) &&
                !PortablePathSafety.ContainsReparsePoint(session))
            {
                Directory.Delete(session, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Incomplete files retain only randomized temporary names under Replica's Temp root.
        }
    }
}
