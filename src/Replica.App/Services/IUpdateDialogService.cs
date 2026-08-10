using Replica.Core.Updates;

namespace Replica.App.Services;

public interface IUpdateDialogService
{
    bool ConfirmDownload(GitHubReleaseDetails release);

    bool ConfirmInstall(UpdateDownloadResult download);
}

public interface IApplicationLifetime
{
    void Shutdown();
}
