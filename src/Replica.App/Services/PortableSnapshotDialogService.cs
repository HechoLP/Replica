using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace Replica.App.Services;

public sealed class PortableSnapshotDialogService : IPortableSnapshotDialogService
{
    public string? SelectSnapshot()
    {
        OpenFileDialog dialog = new()
        {
            Title = "내보낼 Replica Snapshot 선택",
            Filter = "Replica Snapshot (*.replica)|*.replica",
            CheckFileExists = true,
            Multiselect = false,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? SelectFolder(string title, string? initialDirectory)
    {
        OpenFolderDialog dialog = new()
        {
            Title = title,
            Multiselect = false,
        };
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public bool ConfirmInstallerDownload(string destinationDirectory, string versionDescription) =>
        MessageBox.Show(
            $"공식 HechoLP/Replica GitHub Release의 {versionDescription} ReplicaSetup.exe를 다음 폴더에 저장합니다.\n\n" +
            $"{destinationDirectory}\n\n다운로드만 수행하며 자동 실행하지 않습니다.",
            "Replica 설치 파일 다운로드 승인",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
}
