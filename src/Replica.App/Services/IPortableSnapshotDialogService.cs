namespace Replica.App.Services;

public interface IPortableSnapshotDialogService
{
    string? SelectSnapshot();

    string? SelectFolder(string title, string? initialDirectory);

    string? SelectOfflineInstaller(string? initialDirectory);

    bool ConfirmInstallerDownload(string destinationDirectory, string versionDescription);
}
