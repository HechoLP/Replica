using Replica.Core.Models;
using Replica.Core.Services;
using Replica.Core.Updates;

namespace Replica.Infrastructure.Updates;

public sealed class UpdateCheckService : IUpdateCheckService
{
    private readonly IAppVersionService appVersion;
    private readonly IGitHubReleaseCatalog catalog;
    private readonly IUpdatePreferenceService preferences;

    public UpdateCheckService(
        IAppVersionService appVersion,
        IGitHubReleaseCatalog catalog,
        IUpdatePreferenceService preferences)
    {
        this.appVersion = appVersion;
        this.catalog = catalog;
        this.preferences = preferences;
    }

    public Task<UpdateCheckResult> CheckForUpdatesAsync(
        bool includePrerelease,
        CancellationToken cancellationToken) => CheckForUpdatesAsync(
            includePrerelease ? UpdateChannel.Beta : UpdateChannel.Stable,
            cancellationToken);

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(
        UpdateChannel channel,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(channel))
        {
            throw new ArgumentOutOfRangeException(nameof(channel));
        }

        GitHubReleaseCatalogResult result = await catalog.GetCatalogAsync(cancellationToken)
            .ConfigureAwait(false);
        if (result.Status != GitHubReleaseCatalogStatus.Success)
        {
            return new UpdateCheckResult(
                MapStatus(result.Status),
                appVersion.CurrentVersion,
                null,
                channel,
                result.RetryAtUtc);
        }

        GitHubReleaseDetails? latest = result.Releases
            .Where(release => !release.IsDraft && IsAllowed(channel, release.Version.Channel))
            .OrderByDescending(release => release.Version)
            .ThenByDescending(release => release.PublishedAtUtc)
            .FirstOrDefault();
        if (latest is null)
        {
            return new UpdateCheckResult(
                UpdateCheckStatus.Unavailable,
                appVersion.CurrentVersion,
                null,
                channel);
        }

        SemanticVersion current = SemanticVersion.FromVersion(appVersion.CurrentVersion);
        if (latest.Version.CompareTo(current) <= 0)
        {
            return new UpdateCheckResult(
                UpdateCheckStatus.UpToDate,
                appVersion.CurrentVersion,
                latest,
                channel);
        }

        UpdatePreference preference = await preferences.GetAsync(cancellationToken).ConfigureAwait(false);
        if (latest.TagName.Equals(preference.SkippedVersionTag, StringComparison.OrdinalIgnoreCase))
        {
            return new UpdateCheckResult(
                UpdateCheckStatus.Skipped,
                appVersion.CurrentVersion,
                latest,
                channel);
        }

        int installerCount = latest.Assets.Count(asset =>
            asset.Name.Equals("ReplicaSetup.exe", StringComparison.Ordinal));
        return new UpdateCheckResult(
            installerCount == 1 ? UpdateCheckStatus.UpdateAvailable : UpdateCheckStatus.AssetMissing,
            appVersion.CurrentVersion,
            latest,
            channel);
    }

    private static bool IsAllowed(UpdateChannel selected, UpdateChannel candidate) => selected switch
    {
        UpdateChannel.Stable => candidate == UpdateChannel.Stable,
        UpdateChannel.Beta => candidate is UpdateChannel.Stable or UpdateChannel.Beta,
        UpdateChannel.Alpha => true,
        _ => false,
    };

    private static UpdateCheckStatus MapStatus(GitHubReleaseCatalogStatus status) => status switch
    {
        GitHubReleaseCatalogStatus.RateLimited => UpdateCheckStatus.RateLimited,
        GitHubReleaseCatalogStatus.NetworkUnavailable => UpdateCheckStatus.NetworkUnavailable,
        GitHubReleaseCatalogStatus.TimedOut => UpdateCheckStatus.TimedOut,
        GitHubReleaseCatalogStatus.InvalidResponse => UpdateCheckStatus.InvalidResponse,
        _ => UpdateCheckStatus.Unavailable,
    };
}
