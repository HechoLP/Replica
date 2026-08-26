using System.Security.Cryptography;
using Replica.Core.Portable;
using Replica.Core.Services;

namespace Replica.Infrastructure.Portable;

public sealed class ReplicaInstallerDownloadService : IReplicaInstallerDownloadService
{
    private const long MaximumInstallerSize = 2L * 1024 * 1024 * 1024;
    private const int BufferSize = 128 * 1024;
    private readonly IFileHashService hashes;
    private readonly IOfficialReleaseSource releaseSource;
    private readonly IPortableStorageInspector storageInspector;

    public ReplicaInstallerDownloadService(
        IOfficialReleaseSource releaseSource,
        IPortableStorageInspector storageInspector,
        IFileHashService hashes)
    {
        this.releaseSource = releaseSource;
        this.storageInspector = storageInspector;
        this.hashes = hashes;
    }

    public async Task<ReplicaInstallerDownloadResult> DownloadAsync(
        ReplicaInstallerDownloadRequest request,
        IProgress<ReplicaInstallerDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.UserApproved)
        {
            throw new ReplicaInstallerDownloadException("Installer download requires explicit user approval.");
        }

        string destinationDirectory = ValidateDestination(request.DestinationDirectory);
        string destinationPath = Path.Combine(destinationDirectory, "ReplicaSetup.exe");
        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
        {
            throw new ReplicaInstallerDownloadException("ReplicaSetup.exe already exists in the selected folder.");
        }

        progress?.Report(new ReplicaInstallerDownloadProgress(
            ReplicaInstallerDownloadStage.ResolvingRelease,
            0,
            0));
        IReadOnlyList<OfficialReplicaRelease> releases = await releaseSource
            .GetReleasesAsync(cancellationToken)
            .ConfigureAwait(false);
        OfficialReplicaRelease release = SelectRelease(releases, request);
        OfficialReleaseAsset[] installerAssets = release.Assets.Where(item =>
                item.Name.Equals("ReplicaSetup.exe", StringComparison.Ordinal))
            .ToArray();
        if (installerAssets.Length != 1)
        {
            throw new ReplicaInstallerDownloadException(
                "The selected official release must contain exactly one ReplicaSetup.exe asset.");
        }

        OfficialReleaseAsset asset = installerAssets[0];
        ValidateRelease(release, asset);
        PortableStorageInspection inspection = await storageInspector.InspectAsync(
            destinationDirectory,
            asset.Size,
            cancellationToken).ConfigureAwait(false);
        if (!inspection.CanExport)
        {
            throw new ReplicaInstallerDownloadException(
                inspection.Issues.First(issue => issue.BlocksExport).Message);
        }

        string temporaryPath = Path.Combine(
            destinationDirectory,
            $".ReplicaSetup.{Guid.NewGuid():N}.download");
        bool published = false;
        bool completed = false;
        try
        {
            progress?.Report(new ReplicaInstallerDownloadProgress(
                ReplicaInstallerDownloadStage.Downloading,
                0,
                asset.Size));
            string downloadHash = await DownloadAsync(
                asset,
                temporaryPath,
                progress,
                cancellationToken).ConfigureAwait(false);
            EnsureTemporaryFileSafe(temporaryPath, asset.Size);
            string hash = await hashes.ComputeSha256Async(temporaryPath, cancellationToken)
                .ConfigureAwait(false);
            if (!downloadHash.Equals(hash, StringComparison.OrdinalIgnoreCase))
            {
                throw new ReplicaInstallerDownloadException("ReplicaSetup.exe changed during verification.");
            }

            if (asset.Sha256 is not null &&
                !asset.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase))
            {
                throw new ReplicaInstallerDownloadException("ReplicaSetup.exe failed SHA-256 verification.");
            }

            progress?.Report(new ReplicaInstallerDownloadProgress(
                ReplicaInstallerDownloadStage.Verifying,
                asset.Size,
                asset.Size));
            cancellationToken.ThrowIfCancellationRequested();
            EnsureDestinationStillSafe(destinationDirectory);
            EnsureTemporaryFileSafe(temporaryPath, asset.Size);
            if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
            {
                throw new ReplicaInstallerDownloadException(
                    "ReplicaSetup.exe appeared in the destination during download.");
            }

            File.Move(temporaryPath, destinationPath, overwrite: false);
            published = true;
            EnsureDestinationStillSafe(destinationDirectory);
            EnsureTemporaryFileSafe(destinationPath, asset.Size);
            string publishedHash = await hashes.ComputeSha256Async(destinationPath, cancellationToken)
                .ConfigureAwait(false);
            if (!publishedHash.Equals(hash, StringComparison.OrdinalIgnoreCase) ||
                asset.Sha256 is not null &&
                !asset.Sha256.Equals(publishedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new ReplicaInstallerDownloadException(
                    "ReplicaSetup.exe changed while it was being published.");
            }

            progress?.Report(new ReplicaInstallerDownloadProgress(
                ReplicaInstallerDownloadStage.Completed,
                asset.Size,
                asset.Size));
            completed = true;
            return new ReplicaInstallerDownloadResult(
                destinationPath,
                release.TagName,
                asset.Size,
                publishedHash,
                asset.Sha256 is not null,
                release.ReleasePage);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ReplicaInstallerDownloadException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            throw new ReplicaInstallerDownloadException("ReplicaSetup.exe could not be downloaded safely.", exception);
        }
        finally
        {
            bool cleanupSucceeded = true;
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup; the incomplete file never receives the installer name.
            }

            if (published && !completed)
            {
                try
                {
                    File.Delete(destinationPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    cleanupSucceeded = false;
                }
            }

            if (!cleanupSucceeded)
            {
                throw new ReplicaInstallerDownloadException(
                    "Installer verification failed and the unsafe published file could not be removed.");
            }
        }
    }

    private async Task<string> DownloadAsync(
        OfficialReleaseAsset asset,
        string temporaryPath,
        IProgress<ReplicaInstallerDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using Stream source = await releaseSource.OpenAssetStreamAsync(asset, cancellationToken)
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
            if (total > asset.Size || total > MaximumInstallerSize)
            {
                throw new ReplicaInstallerDownloadException("The installer download exceeded its declared size.");
            }

            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            progress?.Report(new ReplicaInstallerDownloadProgress(
                ReplicaInstallerDownloadStage.Downloading,
                total,
                asset.Size));
        }

        if (total != asset.Size)
        {
            throw new ReplicaInstallerDownloadException("The installer download size did not match its release metadata.");
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        destination.Flush(flushToDisk: true);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static OfficialReplicaRelease SelectRelease(
        IReadOnlyList<OfficialReplicaRelease> releases,
        ReplicaInstallerDownloadRequest request)
    {
        IEnumerable<OfficialReplicaRelease> published = releases.Where(release => !release.IsDraft);
        OfficialReplicaRelease? selected = request.Selection switch
        {
            ReplicaReleaseSelection.LatestStable => published
                .Where(release => !release.IsPrerelease)
                .OrderByDescending(release => release.PublishedAtUtc)
                .FirstOrDefault(),
            ReplicaReleaseSelection.SpecificVersion when !string.IsNullOrWhiteSpace(request.VersionTag) =>
                published.SingleOrDefault(release =>
                    release.TagName.Equals(request.VersionTag, StringComparison.OrdinalIgnoreCase)),
            _ => null,
        };
        return selected ?? throw new ReplicaInstallerDownloadException(
            "The requested official Replica release was not found.");
    }

    private static void ValidateRelease(OfficialReplicaRelease release, OfficialReleaseAsset asset)
    {
        if (asset.Size is <= 0 or > MaximumInstallerSize ||
            !release.ReleasePage.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !release.ReleasePage.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !release.ReleasePage.AbsolutePath.StartsWith("/HechoLP/Replica/releases/", StringComparison.Ordinal) ||
            !asset.DownloadUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !asset.DownloadUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !asset.DownloadUri.AbsolutePath.StartsWith(
                "/HechoLP/Replica/releases/download/",
                StringComparison.Ordinal))
        {
            throw new ReplicaInstallerDownloadException("The selected installer asset is not an official Replica release asset.");
        }
    }

    private static string ValidateDestination(string destinationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        string fullPath = Path.GetFullPath(destinationDirectory);
        if (!Directory.Exists(fullPath) || PortablePathSafety.ContainsReparsePoint(fullPath))
        {
            throw new ReplicaInstallerDownloadException("The installer destination is not safe.");
        }

        return fullPath;
    }

    private static void EnsureDestinationStillSafe(string directoryPath)
    {
        if (!Directory.Exists(directoryPath) || PortablePathSafety.ContainsReparsePoint(directoryPath))
        {
            throw new ReplicaInstallerDownloadException("The installer destination changed during download.");
        }
    }

    private static void EnsureTemporaryFileSafe(string temporaryPath, long expectedSize)
    {
        if (!File.Exists(temporaryPath))
        {
            throw new ReplicaInstallerDownloadException("The temporary installer file disappeared.");
        }

        FileInfo file = new(temporaryPath);
        if (file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file.Length != expectedSize)
        {
            throw new ReplicaInstallerDownloadException("The temporary installer file changed unexpectedly.");
        }
    }
}
