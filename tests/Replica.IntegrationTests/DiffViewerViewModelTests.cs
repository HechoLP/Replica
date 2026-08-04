using Replica.App.ViewModels;
using Replica.Core.Diffing;

namespace Replica.IntegrationTests;

public sealed class DiffViewerViewModelTests
{
    [Fact]
    public void Display_ProjectsScoresAndSafetyMetadataForViewer()
    {
        EnvironmentDiffResult result = new(
            DiffRestoreMode.Exact,
            [
                new DiffItem(
                    DiffType.Extra,
                    DiffArea.Applications,
                    "CONTOSO.EXTRA",
                    "Contoso Extra",
                    null,
                    "1.0.0",
                    null,
                    false,
                    true,
                    false,
                    DiffRiskLevel.High,
                    true,
                    true,
                    0,
                    "ExtraPreservedInExactMode"),
            ],
            new EnvironmentSimilarityScore(
                82,
                80,
                1,
                2,
                0,
                [
                    new DiffCategoryScore(
                        DiffScoreCategory.Applications,
                        30,
                        90,
                        4,
                        1),
                ]));
        DiffViewerViewModel viewModel = new();

        viewModel.Display(result);

        Assert.Equal("전체 환경 일치율: 82%", viewModel.OverallScoreText);
        Assert.Contains("80%", viewModel.CoverageText, StringComparison.Ordinal);
        Assert.Equal("프로그램", Assert.Single(viewModel.CategoryScores).Name);
        DiffItemRowViewModel row = Assert.Single(viewModel.Items);
        Assert.Equal("추가", row.Type);
        Assert.Equal("현재 항목 유지", row.AutomaticRestore);
        Assert.Equal("높음", row.Risk);
        Assert.Equal("필요", row.Administrator);
        Assert.Equal("필요", row.Restart);
    }
}
