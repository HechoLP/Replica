namespace Replica.App.ViewModels;

internal static class ReasonCodePresenter
{
    public static string GetText(string? reasonCode) => reasonCode switch
    {
        null or "" => "—",
        "ExactMatch" => "현재 상태와 같습니다.",
        "ActionNotSelected" => "사용자가 선택하지 않았습니다.",
        "ManualActionRequired" or "ManualValueHandlerRequired" => "사용자가 직접 확인해야 합니다.",
        "SensitiveValueExcluded" or "SensitivePathExcluded" or "SensitiveFile" =>
            "민감 데이터 보호 정책에 따라 제외했습니다.",
        "MissingApplicationCanInstall" => "Snapshot의 프로그램을 설치할 수 있습니다.",
        "MissingApplicationRequiresReview" => "프로그램 설치 전 확인이 필요합니다.",
        "MissingApplicationUnsupported" or "ApplicationUnsupported" =>
            "자동 복원을 지원하지 않는 프로그램입니다.",
        "MissingPathCanRestore" or "MissingValueCanRestore" => "Snapshot 값으로 복원할 수 있습니다.",
        "MissingPathRequiresReview" or "MissingValueRequiresReview" or
        "ValueMismatchRequiresReview" => "복원 전 현재 값과 목표 값을 확인해야 합니다.",
        "ValueMismatchCanRestore" => "현재 값이 달라 Snapshot 값으로 복원할 수 있습니다.",
        "PathOrderMismatch" => "PATH 순서가 Snapshot과 다릅니다.",
        "ExtraPathPreserved" or "ExtraValuePreserved" or
        "ExtraPreservedInExactMode" or "ExtraPreservedNoAutomaticRemoval" =>
            "현재 PC에만 있는 항목은 자동으로 제거하지 않습니다.",
        "PathProviderUnsupported" or "ValueProviderUnsupported" =>
            "이 항목은 자동 복원을 지원하지 않습니다.",
        "ActionCancelled" or "ExecutionCancelled" or "UserCancelled" or "ElevationCancelled" =>
            "사용자 요청으로 중단했습니다.",
        "JournalSafetyStop" or "RecoveryJournalFinalizationFailed" =>
            "롤백 기록을 안전하게 확정하지 못해 다음 변경을 중단했습니다.",
        "ActionExecutionFailed" or "ElevatedActionFailed" or "FileRestoreFailed" or
        "WinGetInstallFailed" or "PackageVerificationFailed" or "RollbackItemFailed" =>
            "작업을 안전하게 완료하지 못했습니다.",
        "RestoredFileHashMismatch" or "HandlerResultMismatch" =>
            "완료 결과가 예상한 안전 조건과 일치하지 않습니다.",
        "RollbackVerified" => "원래 상태 복원을 확인했습니다.",
        _ => "세부 안전 사유는 작업 메시지와 로그에서 확인하세요.",
    };
}
