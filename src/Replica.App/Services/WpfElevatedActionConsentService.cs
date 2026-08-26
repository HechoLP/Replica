using System.Windows;
using Replica.Core.Planning;
using Replica.Core.Services;

namespace Replica.App.Services;

public sealed class WpfElevatedActionConsentService : IElevatedActionConsentService
{
    public Task<bool> ConfirmAsync(
        RestoreAction action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        string operation = action.Type switch
        {
            RestoreActionType.SetMachineEnvironmentVariable => "컴퓨터 환경 변수 변경",
            RestoreActionType.AddPathEntry => "컴퓨터 PATH 항목 추가",
            RestoreActionType.RestoreRegistryValue => "허용된 레지스트리 값 복원",
            _ => throw new InvalidOperationException("이 작업은 관리자 실행이 허용되지 않습니다."),
        };
        string target = Sanitize(action.SourceDiffKey);
        string value = Sanitize(action.TargetValue ?? "(값 없음)");
        MessageBoxResult result = MessageBox.Show(
            $"Replica가 다음 관리자 작업을 실행하려고 합니다.\n\n" +
            $"작업: {operation}\n대상: {target}\n새 값: {value}\n\n" +
            "방금 Replica에서 이 정확한 작업을 승인하지 않았다면 '아니요'를 선택하세요.",
            "Replica 관리자 작업 최종 확인",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No,
            MessageBoxOptions.None);
        return Task.FromResult(result == MessageBoxResult.Yes);
    }

    private static string Sanitize(string value)
    {
        char[] safe = value
            .Where(character => !char.IsControl(character))
            .Take(512)
            .ToArray();
        return safe.Length == 0 ? "(표시할 수 없음)" : new string(safe);
    }
}
