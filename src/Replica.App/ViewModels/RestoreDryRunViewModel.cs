using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Replica.Core.Diffing;
using Replica.Core.Planning;

namespace Replica.App.ViewModels;

public sealed partial class RestoreDryRunViewModel : ObservableObject
{
    private RestorePlan? _pendingPlan;

    [ObservableProperty]
    private IReadOnlyList<RestoreActionRowViewModel> _actions = [];

    [ObservableProperty]
    private IReadOnlyList<RestoreSummaryRowViewModel> _summary = [];

    [ObservableProperty]
    private string _statusText = "Restore Plan을 생성하면 실행 전 검토 내용이 표시됩니다.";

    [ObservableProperty]
    private DiffRestoreMode _selectedMode = DiffRestoreMode.Safe;

    public IReadOnlyList<DiffRestoreMode> Modes { get; } = Enum.GetValues<DiffRestoreMode>();

    public event Action<DiffRestoreMode>? ModeSelectionRequested;

    public bool CanReview => _pendingPlan?.ReviewStatus == RestorePlanReviewStatus.PendingReview;

    public RestorePlan? ReviewedPlan { get; private set; }

    public void Invalidate(string statusText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statusText);
        _pendingPlan = null;
        ReviewedPlan = null;
        Summary = [];
        Actions = [];
        StatusText = statusText;
        OnPropertyChanged(nameof(CanReview));
    }

    public void Display(RestorePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        _pendingPlan = plan;
        SelectedMode = plan.Mode;
        ReviewedPlan = null;
        Summary = CreateSummary(plan.DryRun);
        Actions = plan.Actions.Select(CreateRow).ToArray();
        StatusText = $"{plan.Mode} Restore Plan · 사용자 확인 전 · " +
            $"기본 선택 {plan.DryRun.SelectedActionCount:N0}개";
        OnPropertyChanged(nameof(CanReview));
    }

    [RelayCommand]
    private void SelectMode(DiffRestoreMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            return;
        }

        SelectedMode = mode;
        StatusText = mode switch
        {
            DiffRestoreMode.Safe => "Safe: Missing만 기본 선택하며 Extra·다운그레이드·제거를 자동 실행하지 않습니다.",
            DiffRestoreMode.Recommended => "Recommended: 호환 가능한 업데이트와 설정·환경 병합을 제안합니다.",
            _ => "Exact: 가능한 차이를 모두 표시하지만 제거·다운그레이드·고위험 작업은 수동입니다.",
        };
        ModeSelectionRequested?.Invoke(mode);
    }

    [RelayCommand]
    private void ConfirmPlan()
    {
        if (_pendingPlan is null || !CanReview)
        {
            return;
        }

        ReviewedPlan = _pendingPlan.Approve();
        _pendingPlan = ReviewedPlan;
        StatusText = "Restore Plan 검토를 승인했습니다. 아직 시스템 변경은 실행되지 않았습니다.";
        OnPropertyChanged(nameof(CanReview));
    }

    [RelayCommand]
    private void RejectPlan()
    {
        if (_pendingPlan is null || !CanReview)
        {
            return;
        }

        ReviewedPlan = _pendingPlan.Reject();
        _pendingPlan = ReviewedPlan;
        StatusText = "Restore Plan을 취소했습니다. 시스템은 변경되지 않았습니다.";
        OnPropertyChanged(nameof(CanReview));
    }

    private static IReadOnlyList<RestoreSummaryRowViewModel> CreateSummary(
        RestoreDryRunSummary summary)
    {
        return
        [
            new("설치 프로그램", summary.InstallPackageCount.ToString("N0")),
            new("업데이트 프로그램", summary.UpdatePackageCount.ToString("N0")),
            new("복원 설정", summary.RestoreSettingsCount.ToString("N0")),
            new("복원 사용자 파일", summary.RestoreSelectedUserFileCount.ToString("N0")),
            new("환경변수 변경", summary.EnvironmentChangeCount.ToString("N0")),
            new("관리자 권한", summary.AdministratorActionCount.ToString("N0")),
            new("재부팅", summary.RequiresRestart ? "필요" : "불필요"),
            new("롤백 가능", summary.RollbackCapableActionCount.ToString("N0")),
            new("수동 작업", summary.ManualActionCount.ToString("N0")),
            new("예상 시간", FormatDuration(summary.EstimatedDuration)),
            new("예상 다운로드", FormatBytes(summary.EstimatedDownloadBytes)),
        ];
    }

    private static RestoreActionRowViewModel CreateRow(RestoreAction action)
    {
        return new RestoreActionRowViewModel(
            GetActionTypeName(action.Type),
            action.Name,
            action.Description,
            action.OriginalValue ?? "—",
            action.CurrentValue ?? "—",
            action.TargetValue ?? "—",
            GetRiskName(action.Risk),
            action.RequiresAdministrator ? "필요" : "불필요",
            action.RequiresRestart ? "필요" : "불필요",
            action.CanRollback ? "가능" : "불가",
            action.Dependencies.Count.ToString("N0"),
            FormatDuration(action.EstimatedDuration),
            FormatBytes(action.EstimatedDownloadBytes),
            action.IsSelected ? "선택" : "미선택",
            action.IsManualOnly ? "수동" : "자동화 지원",
            action.ReasonCode);
    }

    private static string GetActionTypeName(RestoreActionType type)
    {
        return type switch
        {
            RestoreActionType.InstallPackage => "프로그램 설치",
            RestoreActionType.UpdatePackage => "프로그램 업데이트",
            RestoreActionType.RestoreFile => "파일 복원",
            RestoreActionType.MergeJson => "JSON 병합",
            RestoreActionType.SetUserEnvironmentVariable => "사용자 환경변수",
            RestoreActionType.SetMachineEnvironmentVariable => "시스템 환경변수",
            RestoreActionType.AddPathEntry => "PATH 추가",
            RestoreActionType.RestoreRegistryValue => "레지스트리 복원",
            RestoreActionType.InstallExtension => "확장 설치",
            RestoreActionType.InstallPowerShellModule => "PowerShell 모듈",
            RestoreActionType.RestoreSelectedUserFile => "사용자 파일 복원",
            RestoreActionType.ManualInstruction => "수동 작업",
            RestoreActionType.Validate => "검증",
            RestoreActionType.RestartRequired => "재부팅 필요",
            _ => type.ToString(),
        };
    }

    private static string GetRiskName(DiffRiskLevel risk)
    {
        return risk switch
        {
            DiffRiskLevel.None => "없음",
            DiffRiskLevel.Low => "낮음",
            DiffRiskLevel.Medium => "보통",
            DiffRiskLevel.High => "높음",
            DiffRiskLevel.Critical => "매우 높음",
            _ => risk.ToString(),
        };
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return "즉시";
        }

        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}시간 {duration.Minutes}분";
        }

        return duration.TotalMinutes >= 1
            ? $"{(int)duration.TotalMinutes}분 {duration.Seconds}초"
            : $"{duration.Seconds}초";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.#} {units[unit]}";
    }
}

public sealed record RestoreSummaryRowViewModel(string Name, string Value);

public sealed record RestoreActionRowViewModel(
    string Type,
    string Name,
    string Description,
    string OriginalValue,
    string CurrentValue,
    string TargetValue,
    string Risk,
    string Administrator,
    string Restart,
    string Rollback,
    string DependencyCount,
    string EstimatedDuration,
    string EstimatedDownload,
    string Selection,
    string Handling,
    string Reason);
