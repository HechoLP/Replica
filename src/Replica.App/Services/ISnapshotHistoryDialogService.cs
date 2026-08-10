namespace Replica.App.Services;

public enum SnapshotDeleteChoice
{
    Cancel,
    RemoveHistoryOnly,
    DeleteSnapshotFile,
}

public interface ISnapshotHistoryDialogService
{
    SnapshotDeleteChoice ConfirmDelete(string snapshotName, bool fileExists);
}
