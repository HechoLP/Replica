using Replica.Core.Models;
using Replica.Core.Services;

namespace Replica.Infrastructure.Updates;

public sealed class GitHubReleaseProvider : IReleaseProvider
{
    private readonly IOfficialReleaseSource source;

    public GitHubReleaseProvider(IOfficialReleaseSource source)
    {
        this.source = source;
    }

    public async Task<ReleaseInfo?> GetLatestReleaseAsync(
        bool includePrerelease,
        CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<Replica.Core.Portable.OfficialReplicaRelease> releases = await source
                .GetReleasesAsync(cancellationToken)
                .ConfigureAwait(false);
            Replica.Core.Portable.OfficialReplicaRelease? release = releases
                .Where(item => !item.IsDraft && (includePrerelease || !item.IsPrerelease))
                .OrderByDescending(item => item.PublishedAtUtc)
                .FirstOrDefault();
            return release is null
                ? null
                : new ReleaseInfo(
                    release.Version,
                    release.TagName,
                    release.ReleasePage,
                    release.IsPrerelease);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or Replica.Core.Portable.ReplicaInstallerDownloadException)
        {
            return null;
        }
    }
}
