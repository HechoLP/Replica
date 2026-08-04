using Replica.Core.Models;
using Replica.Core.Services;

namespace Replica.Infrastructure.Updates;

public sealed class UpdateCheckService : IUpdateCheckService
{
    private readonly IAppVersionService _appVersion;
    private readonly IReleaseProvider _releaseProvider;
    private readonly IReleaseVersionComparer _versionComparer;

    public UpdateCheckService(
        IAppVersionService appVersion,
        IReleaseProvider releaseProvider,
        IReleaseVersionComparer versionComparer)
    {
        _appVersion = appVersion;
        _releaseProvider = releaseProvider;
        _versionComparer = versionComparer;
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(
        bool includePrerelease,
        CancellationToken cancellationToken)
    {
        ReleaseInfo? release = await _releaseProvider
            .GetLatestReleaseAsync(includePrerelease, cancellationToken)
            .ConfigureAwait(false);

        if (release is null)
        {
            return new UpdateCheckResult(
                UpdateCheckStatus.Unavailable,
                _appVersion.CurrentVersion,
                null);
        }

        UpdateCheckStatus status = _versionComparer.IsNewer(
            release.Version,
            _appVersion.CurrentVersion)
            ? UpdateCheckStatus.UpdateAvailable
            : UpdateCheckStatus.UpToDate;

        return new UpdateCheckResult(status, _appVersion.CurrentVersion, release);
    }
}
