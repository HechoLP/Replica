using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Replica.Mac.Services;

public interface IMacFileDialogService
{
    Task<string?> PickSnapshotToOpenAsync(CancellationToken cancellationToken);

    Task<string?> PickSnapshotDestinationAsync(string suggestedName, CancellationToken cancellationToken);

    Task<bool> OpenUriAsync(Uri uri);
}

public sealed class MacFileDialogService : IMacFileDialogService
{
    private static readonly FilePickerFileType SnapshotFileType = new("Replica Snapshot")
    {
        Patterns = ["*.replica"],
        MimeTypes = ["application/x-replica-snapshot"],
    };

    private TopLevel? owner;

    public void Attach(TopLevel topLevel)
    {
        ArgumentNullException.ThrowIfNull(topLevel);
        owner = topLevel;
    }

    public async Task<string?> PickSnapshotToOpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TopLevel topLevel = GetOwner();
        IReadOnlyList<IStorageFile> files = await topLevel.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "Replica Snapshot 열기",
                AllowMultiple = false,
                FileTypeFilter = [SnapshotFileType],
            });
        cancellationToken.ThrowIfCancellationRequested();
        return files.Count == 1 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickSnapshotDestinationAsync(
        string suggestedName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestedName);
        cancellationToken.ThrowIfCancellationRequested();
        TopLevel topLevel = GetOwner();
        IStorageFile? file = await topLevel.StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = "Lightweight Snapshot 저장",
                SuggestedFileName = suggestedName,
                DefaultExtension = "replica",
                FileTypeChoices = [SnapshotFileType],
                ShowOverwritePrompt = true,
            });
        cancellationToken.ThrowIfCancellationRequested();
        return file?.TryGetLocalPath();
    }

    public Task<bool> OpenUriAsync(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            return Task.FromResult(false);
        }

        return GetOwner().Launcher.LaunchUriAsync(uri);
    }

    private TopLevel GetOwner()
    {
        return owner ?? throw new InvalidOperationException("The file dialog service is not attached to a window.");
    }
}
