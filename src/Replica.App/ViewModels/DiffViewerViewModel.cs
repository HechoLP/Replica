using CommunityToolkit.Mvvm.ComponentModel;
using Replica.Core.Diffing;

namespace Replica.App.ViewModels;

public sealed partial class DiffViewerViewModel : ObservableObject
{
    [ObservableProperty]
    private IReadOnlyList<DiffScoreRowViewModel> _categoryScores = [];

    [ObservableProperty]
    private string _coverageText = "비교 범위: 비교 전";

    [ObservableProperty]
    private IReadOnlyList<DiffItemRowViewModel> _items = [];

    [ObservableProperty]
    private string _overallScoreText = "전체 환경 일치율: 비교 전";

    [ObservableProperty]
    private string _statusText = "Snapshot과 현재 PC를 비교하면 차이가 여기에 표시됩니다.";

    public void Display(EnvironmentDiffResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        OverallScoreText = result.Similarity.OverallScore is int score
            ? $"전체 환경 일치율: {score}%"
            : "전체 환경 일치율: 비교 가능한 항목 없음";
        CoverageText = $"비교 범위: {result.Similarity.CoveragePercent}% · " +
            $"Unsupported {result.Similarity.UnsupportedCount} · " +
            $"민감 제외 {result.Similarity.SensitiveExcludedCount} · " +
            $"오류 {result.Similarity.ErrorCount}";
        CategoryScores = result.Similarity.Categories
            .Select(category => new DiffScoreRowViewModel(
                GetCategoryName(category.Category),
                category.Score is int categoryScore ? $"{categoryScore}%" : "비교 불가",
                category.Weight,
                category.ComparableItemCount,
                category.ExcludedItemCount))
            .ToArray();
        Items = result.Items
            .Select(item => new DiffItemRowViewModel(
                GetTypeName(item.Type),
                GetAreaName(item.Area),
                item.DisplayName,
                item.SourceValue ?? "—",
                item.TargetValue ?? "—",
                item.PreserveTarget
                    ? "현재 항목 유지"
                    : item.CanAutomaticallyRestore ? "가능" : "불가",
                GetRiskName(item.Risk),
                item.RequiresAdministrator ? "필요" : "불필요",
                item.RequiresRestart ? "필요" : "불필요",
                $"{item.SimilarityPercent}%",
                item.ReasonCode))
            .ToArray();
        StatusText = result.Items.Count == 0
            ? "비교 항목이 없습니다."
            : $"{result.Items.Count:N0}개 비교 항목을 검토할 수 있습니다.";
    }

    private static string GetTypeName(DiffType type)
    {
        return type switch
        {
            DiffType.ExactMatch => "일치",
            DiffType.Missing => "누락",
            DiffType.Extra => "추가",
            DiffType.VersionMismatch => "버전 차이",
            DiffType.ValueMismatch => "설정 차이",
            DiffType.FileChanged => "파일 차이",
            DiffType.Conflict => "충돌",
            DiffType.Unsupported => "지원 안 함",
            DiffType.SensitiveExcluded => "민감 제외",
            DiffType.ManualActionRequired => "수동 작업 필요",
            DiffType.Error => "오류",
            _ => type.ToString(),
        };
    }

    private static string GetAreaName(DiffArea area)
    {
        return area switch
        {
            DiffArea.Applications => "프로그램",
            DiffArea.StoreApplications => "Store 앱",
            DiffArea.EnvironmentVariables => "환경변수",
            DiffArea.Path => "PATH",
            DiffArea.Fonts => "글꼴",
            DiffArea.WindowsInformation => "Windows 정보",
            DiffArea.PluginSettings => "플러그인 설정",
            DiffArea.ConfigurationFiles => "설정 파일",
            DiffArea.SelectedUserFiles => "선택 파일",
            DiffArea.DevelopmentEnvironment => "개발환경",
            _ => area.ToString(),
        };
    }

    private static string GetCategoryName(DiffScoreCategory category)
    {
        return category switch
        {
            DiffScoreCategory.Applications => "프로그램",
            DiffScoreCategory.DevelopmentEnvironment => "개발환경",
            DiffScoreCategory.ApplicationSettings => "프로그램 설정",
            DiffScoreCategory.EnvironmentAndPath => "환경변수 및 PATH",
            DiffScoreCategory.FontsAndOther => "글꼴 및 기타",
            _ => category.ToString(),
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
}

public sealed record DiffScoreRowViewModel(
    string Name,
    string Score,
    int Weight,
    int ComparableCount,
    int ExcludedCount);

public sealed record DiffItemRowViewModel(
    string Type,
    string Area,
    string Name,
    string Source,
    string Target,
    string AutomaticRestore,
    string Risk,
    string Administrator,
    string Restart,
    string Similarity,
    string Reason);
