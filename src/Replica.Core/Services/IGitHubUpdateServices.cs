using Replica.Core.Updates;

namespace Replica.Core.Services;

public interface IGitHubReleaseCatalog
{
    Task<GitHubReleaseCatalogResult> GetCatalogAsync(CancellationToken cancellationToken);
}

public interface IUpdatePreferenceService
{
    Task<UpdatePreference> GetAsync(CancellationToken cancellationToken);

    Task SaveAsync(UpdatePreference preference, CancellationToken cancellationToken);
}

public interface IUpdateDownloadService
{
    Task<UpdateDownloadResult> DownloadAsync(
        UpdateDownloadRequest request,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken);
}

public interface IUpdateInstallerService
{
    Task<UpdateInstallerLaunchResult> LaunchAsync(
        UpdateInstallerLaunchRequest request,
        CancellationToken cancellationToken);
}
