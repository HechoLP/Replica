using CommunityToolkit.Mvvm.ComponentModel;
using Replica.Core.Diffing;

namespace Replica.App.ViewModels;

public sealed partial class DiffViewerViewModel : ObservableObject
{
    private IReadOnlyList<DiffItemRowViewModel> allItems = [];
    private EnvironmentDiffResult? lastResult;

    [ObservableProperty]
    private IReadOnlyList<DiffScoreRowViewModel> _categoryScores = [];

    [ObservableProperty]
    private string _coverageText = "비교 범위: 비교 전";

    [ObservableProperty]
    private IReadOnlyList<DiffItemRowViewModel> _items = [];

    [ObservableProperty]
    private IReadOnlyList<DiffTreeGroupViewModel> _treeGroups = [];

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _selectedFilter = "전체";

    [ObservableProperty]
    private string _overallScoreText = "전체 환경 일치율: 비교 전";

    [ObservableProperty]
    private string _statusText = "Snapshot과 현재 PC를 비교하면 차이가 여기에 표시됩니다.";

    public IReadOnlyList<string> Filters { get; } =
    [
        "전체",
        "누락",
        "버전 차이",
        "설정 차이",
        "파일 차이",
        "추가",
        "지원 안 함",
        "수동 작업 필요",
    ];

    public event Action? SelectionChanged;

    public void Clear(string statusText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statusText);
        lastResult = null;
        allItems = [];
        Items = [];
        TreeGroups = [];
        CategoryScores = [];
        OverallScoreText = "전체 환경 일치율: 비교 전";
        CoverageText = "비교 범위: 비교 전";
        StatusText = statusText;
    }

    public void Display(
        EnvironmentDiffResult result,
        IReadOnlySet<DiffSelectionKey>? preservedSelection = null)
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
        lastResult = result;
        allItems = result.Items
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
                item.ReasonCode,
                GetRecommendation(item),
                item.Area,
                item.Key)
            {
                SelectionChanged = () => SelectionChanged?.Invoke(),
                IsSelected = preservedSelection?.Contains(new DiffSelectionKey(item.Area, item.Key)) ??
                    item.Type != DiffType.ExactMatch,
            })
            .ToArray();
        ApplyFilter();
        StatusText = result.Items.Count == 0
            ? "비교 항목이 없습니다."
            : $"{result.Items.Count:N0}개 비교 항목을 검토할 수 있습니다.";
    }

    public IReadOnlySet<DiffSelectionKey> GetSelectedReferences() => allItems
        .Where(item => item.IsSelected)
        .Select(item => new DiffSelectionKey(item.SourceArea, item.SourceKey))
        .ToHashSet();

    public EnvironmentDiffResult GetSelectedDiff(DiffRestoreMode mode)
    {
        if (lastResult is null)
        {
            throw new InvalidOperationException("A Snapshot comparison must be displayed first.");
        }

        IReadOnlySet<DiffSelectionKey> selected = GetSelectedReferences();
        return lastResult with
        {
            Mode = mode,
            Items = lastResult.Items.Where(item =>
                selected.Contains(new DiffSelectionKey(item.Area, item.Key))).ToArray(),
        };
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        IEnumerable<DiffItemRowViewModel> filtered = allItems;
        if (!SelectedFilter.Equals("전체", StringComparison.Ordinal))
        {
            filtered = filtered.Where(item => item.Type.Equals(SelectedFilter, StringComparison.Ordinal));
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            filtered = filtered.Where(item =>
                item.Name.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase) ||
                item.Area.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase) ||
                item.Source.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase) ||
                item.Target.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase));
        }

        Items = filtered.ToArray();
        TreeGroups = Items
            .GroupBy(item => item.Area, StringComparer.CurrentCulture)
            .Select(group => new DiffTreeGroupViewModel(
                group.Key,
                group.Select(item => new DiffTreeItemViewModel(
                    item.Name,
                    item.Type,
                    item.Risk)).ToArray()))
            .ToArray();
    }

    private static string GetRecommendation(DiffItem item)
    {
        if (item.PreserveTarget)
        {
            return "현재 PC 항목 유지";
        }

        if (item.Type is DiffType.Unsupported or DiffType.ManualActionRequired)
        {
            return "수동 작업 검토";
        }

        return item.CanAutomaticallyRestore ? "Restore Plan에 추가 가능" : "사용자 선택 필요";
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
    string Reason,
    string Recommendation,
    DiffArea SourceArea,
    string SourceKey)
{
    private bool isSelected = true;

    public Action? SelectionChanged { get; init; }

    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (isSelected == value)
            {
                return;
            }

            isSelected = value;
            SelectionChanged?.Invoke();
        }
    }
}

public sealed record DiffSelectionKey(DiffArea Area, string Key);

public sealed record DiffTreeGroupViewModel(
    string Name,
    IReadOnlyList<DiffTreeItemViewModel> Items);

public sealed record DiffTreeItemViewModel(string Name, string Type, string Risk);
