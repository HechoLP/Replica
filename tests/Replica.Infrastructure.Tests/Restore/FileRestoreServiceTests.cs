using System.Security.Cryptography;
using System.Text;
using Replica.Core.Execution;
using Replica.Infrastructure.Restore;

namespace Replica.Infrastructure.Tests.Restore;

public sealed class FileRestoreServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"ReplicaFileRestoreTests-{Guid.NewGuid():N}");

    [Fact]
    public async Task RestoreAsync_BacksUpAndAtomicallyReplacesExistingFile()
    {
        string snapshotRoot = CreateDirectory("snapshot");
        string destinationRoot = CreateDirectory("destination");
        string rollback = Path.Combine(_root, "rollback");
        string source = WriteFile(snapshotRoot, "files/project/config.txt", "snapshot-content");
        string destination = WriteFile(destinationRoot, "config.txt", "current-content");
        FileRestoreService service = new();

        FileRestoreResult result = await service.RestoreAsync(
            Request(
                snapshotRoot,
                "files/project/config.txt",
                destination,
                destinationRoot,
                HashFile(source),
                rollback,
                RestoreFileConflictBehavior.OverwriteWithSnapshot),
            CancellationToken.None);

        Assert.Equal(RestoreExecutionState.Succeeded, result.State);
        Assert.Equal("snapshot-content", File.ReadAllText(destination));
        Assert.NotNull(result.BackupPath);
        Assert.Equal("current-content", File.ReadAllText(result.BackupPath));
        Assert.Empty(Directory.EnumerateFiles(destinationRoot, "*.replica.tmp"));
    }

    [Fact]
    public async Task RestoreAsync_RejectsSnapshotPathTraversal()
    {
        string snapshotRoot = CreateDirectory("snapshot");
        string destinationRoot = CreateDirectory("destination");
        string outside = WriteFile(_root, "outside.txt", "outside");
        FileRestoreService service = new();

        await Assert.ThrowsAsync<ArgumentException>(() => service.RestoreAsync(
            Request(
                snapshotRoot,
                "../outside.txt",
                Path.Combine(destinationRoot, "outside.txt"),
                destinationRoot,
                HashFile(outside),
                Path.Combine(_root, "rollback"),
                RestoreFileConflictBehavior.RenameAndKeepBoth),
            CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(destinationRoot, "outside.txt")));
    }

    [Fact]
    public async Task RestoreAsync_PromptsForUserFileConflictWithoutMutation()
    {
        string snapshotRoot = CreateDirectory("snapshot");
        string destinationRoot = CreateDirectory("destination");
        string source = WriteFile(snapshotRoot, "files/save/game.sav", "snapshot-save");
        string destination = WriteFile(destinationRoot, "game.sav", "current-save");
        FileRestoreService service = new();

        FileRestoreResult result = await service.RestoreAsync(
            Request(
                snapshotRoot,
                "files/save/game.sav",
                destination,
                destinationRoot,
                HashFile(source),
                Path.Combine(_root, "rollback"),
                RestoreFileConflictBehavior.PromptForEachConflict),
            CancellationToken.None);

        Assert.Equal(RestoreExecutionState.Skipped, result.State);
        Assert.True(result.ConflictRequiresConfirmation);
        Assert.Equal("current-save", File.ReadAllText(destination));
        Assert.False(Directory.Exists(Path.Combine(_root, "rollback")));
    }

    [Fact]
    public async Task RestoreAsync_RenamesSelectedUserFileAndKeepsBoth()
    {
        string snapshotRoot = CreateDirectory("snapshot");
        string destinationRoot = CreateDirectory("destination");
        string source = WriteFile(snapshotRoot, "files/save/game.sav", "snapshot-save");
        string destination = WriteFile(destinationRoot, "game.sav", "current-save");
        FileRestoreService service = new();

        FileRestoreResult result = await service.RestoreAsync(
            Request(
                snapshotRoot,
                "files/save/game.sav",
                destination,
                destinationRoot,
                HashFile(source),
                Path.Combine(_root, "rollback"),
                RestoreFileConflictBehavior.RenameAndKeepBoth),
            CancellationToken.None);

        Assert.Equal(RestoreExecutionState.Succeeded, result.State);
        Assert.Equal("current-save", File.ReadAllText(destination));
        Assert.NotEqual(destination, result.RestoredPath);
        Assert.Equal("snapshot-save", File.ReadAllText(result.RestoredPath!));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private string CreateDirectory(string relativePath)
    {
        string path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string WriteFile(string root, string relativePath, string content)
    {
        string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }

    private static string HashFile(string path)
    {
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    }

    private static FileRestoreRequest Request(
        string snapshotRoot,
        string sourceRelativePath,
        string destination,
        string destinationRoot,
        string hash,
        string rollback,
        RestoreFileConflictBehavior conflictBehavior)
    {
        return new FileRestoreRequest(
            snapshotRoot,
            sourceRelativePath,
            destination,
            [destinationRoot],
            hash,
            10 * 1024 * 1024,
            true,
            conflictBehavior,
            rollback);
    }
}
