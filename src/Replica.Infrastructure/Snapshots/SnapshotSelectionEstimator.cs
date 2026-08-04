using Replica.Core.Services;
using Replica.Core.Snapshots;

namespace Replica.Infrastructure.Snapshots;

public sealed class SnapshotSelectionEstimator : ISnapshotSelectionEstimator
{
    private readonly SnapshotSelectionPolicy selectionPolicy;

    public SnapshotSelectionEstimator()
        : this(new SnapshotSelectionPolicy())
    {
    }

    internal SnapshotSelectionEstimator(SnapshotSelectionPolicy selectionPolicy)
    {
        this.selectionPolicy = selectionPolicy;
    }

    public Task<ReplicaSelectionEstimate> EstimateAsync(
        IReadOnlyList<ReplicaSelectedFolder> selectedFolders,
        IReadOnlyList<ReplicaOfflineInstaller> offlineInstallers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selectedFolders);
        ArgumentNullException.ThrowIfNull(offlineInstallers);
        ReplicaSelectedFolder[] selectedFolderSnapshot = selectedFolders.ToArray();
        ReplicaOfflineInstaller[] offlineInstallerSnapshot = offlineInstallers.ToArray();

        return Task.Run(
            () => Estimate(selectedFolderSnapshot, offlineInstallerSnapshot, cancellationToken),
            cancellationToken);
    }

    private ReplicaSelectionEstimate Estimate(
        IReadOnlyList<ReplicaSelectedFolder> selectedFolders,
        IReadOnlyList<ReplicaOfflineInstaller> offlineInstallers,
        CancellationToken cancellationToken)
    {
        List<ReplicaFileEstimate> files = [];
        List<ReplicaExclusion> exclusions = [];

        foreach (ReplicaSelectedFolder selectedFolder in selectedFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddSelectedFolder(selectedFolder, files, exclusions, cancellationToken);
        }

        foreach (ReplicaOfflineInstaller installer in offlineInstallers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddOfflineInstaller(installer, files, exclusions);
        }

        long totalSize = files.Aggregate(0L, (total, file) => checked(total + file.Size));
        return new ReplicaSelectionEstimate(
            files.OrderBy(file => file.ArchivePath, StringComparer.Ordinal).ToArray(),
            exclusions.ToArray(),
            totalSize);
    }

    private void AddSelectedFolder(
        ReplicaSelectedFolder selectedFolder,
        ICollection<ReplicaFileEstimate> files,
        ICollection<ReplicaExclusion> exclusions,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedFolder.SourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedFolder.ArchivePath);

        string rootPath = Path.GetFullPath(selectedFolder.SourcePath);
        selectionPolicy.ValidateSelectedRoot(rootPath);

        if (!Directory.Exists(rootPath))
        {
            throw new ReplicaSnapshotException("A selected folder does not exist.");
        }

        if (SnapshotPathValidator.ContainsReparsePoint(rootPath))
        {
            throw new ReplicaSnapshotException("A selected folder cannot be a reparse point.");
        }

        Stack<string> pendingDirectories = new();
        pendingDirectories.Push(rootPath);

        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string currentDirectory = pendingDirectories.Pop();

            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(currentDirectory)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                exclusions.Add(new ReplicaExclusion(currentDirectory, "ReadFailure"));
                continue;
            }

            foreach (string entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    exclusions.Add(new ReplicaExclusion(entry, "ReadFailure"));
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    exclusions.Add(new ReplicaExclusion(entry, "ReparsePoint"));
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (selectionPolicy.ShouldExcludeDirectory(entry, out ReplicaExclusion directoryExclusion))
                    {
                        exclusions.Add(directoryExclusion);
                    }
                    else
                    {
                        pendingDirectories.Push(entry);
                    }

                    continue;
                }

                if (selectionPolicy.ShouldExcludeFile(entry, out ReplicaExclusion fileExclusion))
                {
                    exclusions.Add(fileExclusion);
                    continue;
                }

                try
                {
                    FileInfo fileInfo = new(entry);
                    string relativePath = Path.GetRelativePath(rootPath, entry);
                    string archivePath = SnapshotPathValidator.BuildFileEntryPath(
                        selectedFolder.ArchivePath,
                        relativePath);
                    files.Add(new ReplicaFileEstimate(entry, archivePath, fileInfo.Length));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    exclusions.Add(new ReplicaExclusion(entry, "ReadFailure"));
                }
            }
        }
    }

    private void AddOfflineInstaller(
        ReplicaOfflineInstaller installer,
        ICollection<ReplicaFileEstimate> files,
        ICollection<ReplicaExclusion> exclusions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installer.SourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(installer.ArchivePath);

        string sourcePath = Path.GetFullPath(installer.SourcePath);
        if (!File.Exists(sourcePath))
        {
            throw new ReplicaSnapshotException("A selected offline installer does not exist.");
        }

        if (SnapshotPathValidator.ContainsReparsePoint(sourcePath))
        {
            exclusions.Add(new ReplicaExclusion(sourcePath, "ReparsePoint"));
            return;
        }

        selectionPolicy.ValidateOfflineInstaller(sourcePath);
        string archivePath = SnapshotPathValidator.BuildInstallerEntryPath(installer.ArchivePath);
        try
        {
            files.Add(new ReplicaFileEstimate(sourcePath, archivePath, new FileInfo(sourcePath).Length));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            exclusions.Add(new ReplicaExclusion(sourcePath, "ReadFailure"));
        }
    }

}
