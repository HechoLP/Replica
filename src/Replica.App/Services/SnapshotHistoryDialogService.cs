using System.Windows;

namespace Replica.App.Services;

public sealed class SnapshotHistoryDialogService : ISnapshotHistoryDialogService
{
    public SnapshotDeleteChoice ConfirmDelete(string snapshotName, bool fileExists)
    {
        if (!fileExists)
        {
            MessageBoxResult missingResult = MessageBox.Show(
                $"'{snapshotName}' 기록을 삭제하시겠습니까? Snapshot 파일은 이미 존재하지 않습니다.",
                "Snapshot 기록 삭제",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning,
                MessageBoxResult.Cancel);
            return missingResult == MessageBoxResult.OK
                ? SnapshotDeleteChoice.RemoveHistoryOnly
                : SnapshotDeleteChoice.Cancel;
        }

        MessageBoxResult result = MessageBox.Show(
            $"'{snapshotName}'을 기록에서 삭제합니다.\n\n" +
            "예: Snapshot 파일도 삭제\n아니요: 기록만 삭제\n취소: 아무 작업도 하지 않음",
            "Snapshot 삭제 확인",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        return result switch
        {
            MessageBoxResult.Yes => SnapshotDeleteChoice.DeleteSnapshotFile,
            MessageBoxResult.No => SnapshotDeleteChoice.RemoveHistoryOnly,
            _ => SnapshotDeleteChoice.Cancel,
        };
    }
}
