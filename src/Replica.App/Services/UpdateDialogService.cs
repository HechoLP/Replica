using System.Windows;
using Replica.Core.Updates;

namespace Replica.App.Services;

public sealed class UpdateDialogService : IUpdateDialogService
{
    public bool ConfirmDownload(GitHubReleaseDetails release) => MessageBox.Show(
        $"{release.ReleaseName} ({release.TagName})을 다운로드합니다.\n\n" +
        "공식 HechoLP/Replica GitHub Release의 ReplicaSetup.exe만 Temp 폴더에 저장합니다.",
        "업데이트 다운로드 승인",
        MessageBoxButton.YesNo,
        MessageBoxImage.Question,
        MessageBoxResult.No) == MessageBoxResult.Yes;

    public bool ConfirmInstall(UpdateDownloadResult download)
    {
        string checksum = download.ChecksumStatus == UpdateChecksumStatus.Verified
            ? "SHA-256 검증 완료"
            : "공개된 SHA-256 없음 — 계산된 Hash만 표시";
        return MessageBox.Show(
            $"Installer: ReplicaSetup.exe\n" +
            $"버전: {download.Release.TagName}\n" +
            $"크기: {download.FileSize:N0} B\n" +
            $"상태: {checksum}\n" +
            $"SHA-256: {download.Sha256}\n\n" +
            "Installer를 대화형으로 실행한 뒤 Replica를 종료합니다. Silent Install과 자동 권한 상승은 사용하지 않습니다.",
            "Replica 업데이트 설치 확인",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
    }
}

public sealed class WpfApplicationLifetime : IApplicationLifetime
{
    public void Shutdown() => Application.Current.Shutdown();
}
