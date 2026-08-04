using Replica.Core.Diffing;
using Replica.Core.Matching;

namespace Replica.Core.Tests;

public sealed class EnvironmentDiffEngineTests
{
    private readonly EnvironmentDiffEngine _engine = new(
        new ApplicationMatcher(new ApplicationIdentityNormalizer()),
        new ApplicationVersionComparer(),
        new ApplicationAutomationPolicy());

    [Fact]
    public void Compare_ReportsCompleteEnvironmentAsExactMatch()
    {
        DiffEnvironmentState state = State(
            applications: [Application("Git", "2.51.0", "Git.Git")],
            storeApplications: [Application("Terminal", "1.23.0", msixId: "Microsoft.WindowsTerminal_8wekyb3d8bbwe")],
            values:
            [
                Value(DiffArea.EnvironmentVariables, "DOTNET_ENVIRONMENT", "Development"),
                Value(DiffArea.Fonts, "Cascadia Code", "Regular"),
                Value(DiffArea.WindowsInformation, "Edition", "Windows 11 Pro"),
                Value(DiffArea.PluginSettings, "terminal.profile", "PowerShell"),
                Value(DiffArea.ConfigurationFiles, "gitconfig", hash: "ABC123"),
                Value(DiffArea.SelectedUserFiles, "notes", hash: "DEF456"),
                Value(DiffArea.DevelopmentEnvironment, "dotnet", "10.0.302"),
            ],
            paths: [new DiffPathEntry("C:\\Tools", "User", 0)]);

        EnvironmentDiffResult result = _engine.Compare(state, state, DiffRestoreMode.Recommended);

        Assert.All(result.Items, item => Assert.Equal(DiffType.ExactMatch, item.Type));
        Assert.Equal(100, result.Similarity.OverallScore);
        Assert.Equal(100, result.Similarity.CoveragePercent);
    }

    [Fact]
    public void Compare_ReportsMissingApplicationAndAllowsReliableInstall()
    {
        DiffEnvironmentState source = State(
            applications: [Application("Git", "2.51.0", "Git.Git")]);

        DiffItem item = Assert.Single(
            _engine.Compare(source, DiffEnvironmentState.Empty, DiffRestoreMode.Safe).Items);

        Assert.Equal(DiffType.Missing, item.Type);
        Assert.Equal(ApplicationMatchConfidence.Exact, item.MatchConfidence);
        Assert.True(item.CanAutomaticallyRestore);
        Assert.False(item.PreserveTarget);
    }

    [Theory]
    [InlineData(DiffRestoreMode.Safe)]
    [InlineData(DiffRestoreMode.Recommended)]
    [InlineData(DiffRestoreMode.Exact)]
    public void Compare_PreservesExtraApplicationInEveryMode(DiffRestoreMode mode)
    {
        DiffEnvironmentState target = State(
            applications: [Application("Extra Tool", "1.0.0", "Contoso.Extra")]);

        DiffItem item = Assert.Single(
            _engine.Compare(DiffEnvironmentState.Empty, target, mode).Items);

        Assert.Equal(DiffType.Extra, item.Type);
        Assert.True(item.PreserveTarget);
        Assert.False(item.CanAutomaticallyRestore);
        Assert.False(item.AutomaticRemovalSupported);
        Assert.Equal(DiffRiskLevel.High, item.Risk);
        Assert.Equal(
            mode == DiffRestoreMode.Exact
                ? "ExtraPreservedInExactMode"
                : "ExtraPreservedNoAutomaticRemoval",
            item.ReasonCode);
    }

    [Fact]
    public void Compare_ReportsVersionMismatchWithoutAutomaticDowngrade()
    {
        DiffEnvironmentState newerSource = State(
            applications: [Application("Git", "2.51.0", "Git.Git")]);
        DiffEnvironmentState olderTarget = State(
            applications: [Application("Git", "2.50.0", "Git.Git")]);

        DiffItem update = Assert.Single(
            _engine.Compare(newerSource, olderTarget, DiffRestoreMode.Recommended).Items);
        DiffItem downgrade = Assert.Single(
            _engine.Compare(olderTarget, newerSource, DiffRestoreMode.Exact).Items);

        Assert.Equal(DiffType.VersionMismatch, update.Type);
        Assert.True(update.CanAutomaticallyRestore);
        Assert.Equal(50, update.SimilarityPercent);
        Assert.Equal(DiffType.VersionMismatch, downgrade.Type);
        Assert.False(downgrade.CanAutomaticallyRestore);
        Assert.Equal("TargetNewerAutomaticDowngradeBlocked", downgrade.ReasonCode);
    }

    [Fact]
    public void Compare_DetectsPathOrderDifference()
    {
        DiffEnvironmentState source = State(paths:
        [
            new DiffPathEntry("C:\\One", "User", 0),
            new DiffPathEntry("C:\\Two", "User", 1),
        ]);
        DiffEnvironmentState target = State(paths:
        [
            new DiffPathEntry("C:\\Two", "User", 0),
            new DiffPathEntry("C:\\One", "User", 1),
        ]);

        DiffItem item = Assert.Single(
            _engine.Compare(source, target, DiffRestoreMode.Recommended).Items);

        Assert.Equal(DiffType.ValueMismatch, item.Type);
        Assert.Equal("PathOrderMismatch", item.ReasonCode);
    }

    [Fact]
    public void Compare_RedactsSensitiveValuesAndExcludesThemFromScore()
    {
        DiffEnvironmentState source = State(values:
        [
            Value(
                DiffArea.EnvironmentVariables,
                "API_TOKEN",
                "source-secret",
                isSensitiveExcluded: true),
        ]);
        DiffEnvironmentState target = State(values:
        [
            Value(
                DiffArea.EnvironmentVariables,
                "API_TOKEN",
                "target-secret",
                isSensitiveExcluded: true),
        ]);

        EnvironmentDiffResult result = _engine.Compare(source, target, DiffRestoreMode.Safe);
        DiffItem item = Assert.Single(result.Items);

        Assert.Equal(DiffType.SensitiveExcluded, item.Type);
        Assert.Null(item.SourceValue);
        Assert.Null(item.TargetValue);
        Assert.Equal(1, result.Similarity.SensitiveExcludedCount);
        Assert.Null(result.Similarity.OverallScore);
    }

    [Fact]
    public void Compare_RedactsSensitiveValueBeforeReportingDuplicateConflict()
    {
        DiffEnvironmentState source = State(values:
        [
            Value(
                DiffArea.EnvironmentVariables,
                "API_TOKEN",
                "first-secret",
                isSensitiveExcluded: true),
            Value(
                DiffArea.EnvironmentVariables,
                "API_TOKEN",
                "second-secret",
                isSensitiveExcluded: true),
        ]);

        DiffItem item = Assert.Single(
            _engine.Compare(source, DiffEnvironmentState.Empty, DiffRestoreMode.Safe).Items);

        Assert.Equal(DiffType.SensitiveExcluded, item.Type);
        Assert.Null(item.SourceValue);
        Assert.Null(item.TargetValue);
        Assert.Equal("SensitiveValueExcluded", item.ReasonCode);
    }

    [Fact]
    public void Compare_ExcludesUnsupportedItemsFromScore()
    {
        DiffEnvironmentState source = State(values:
        [
            Value(DiffArea.Fonts, "Licensed Font", "Regular", isSupported: false),
        ]);

        EnvironmentDiffResult result = _engine.Compare(
            source,
            DiffEnvironmentState.Empty,
            DiffRestoreMode.Safe);

        Assert.Equal(DiffType.Unsupported, Assert.Single(result.Items).Type);
        Assert.Equal(1, result.Similarity.UnsupportedCount);
        Assert.Null(result.Similarity.OverallScore);
    }

    [Fact]
    public void Compare_ReportsDuplicateSettingAsConflict()
    {
        DiffEnvironmentState source = State(values:
        [
            Value(DiffArea.PluginSettings, "editor.theme", "Light"),
            Value(DiffArea.PluginSettings, "editor.theme", "Dark"),
        ]);

        DiffItem item = Assert.Single(
            _engine.Compare(source, DiffEnvironmentState.Empty, DiffRestoreMode.Exact).Items);

        Assert.Equal(DiffType.Conflict, item.Type);
        Assert.False(item.CanAutomaticallyRestore);
        Assert.Equal("DuplicateOrConflictingValue", item.ReasonCode);
    }

    [Fact]
    public void Compare_ReportsChangedConfigurationAndSelectedFileHashes()
    {
        DiffEnvironmentState source = State(values:
        [
            Value(DiffArea.ConfigurationFiles, "settings.json", hash: "AAAA"),
            Value(DiffArea.SelectedUserFiles, "project/readme.md", hash: "BBBB"),
        ]);
        DiffEnvironmentState target = State(values:
        [
            Value(DiffArea.ConfigurationFiles, "settings.json", hash: "CCCC"),
            Value(DiffArea.SelectedUserFiles, "project/readme.md", hash: "DDDD"),
        ]);

        EnvironmentDiffResult result = _engine.Compare(source, target, DiffRestoreMode.Recommended);

        Assert.Equal(2, result.Items.Count);
        Assert.All(result.Items, item => Assert.Equal(DiffType.FileChanged, item.Type));
    }

    [Fact]
    public void Compare_IsDeterministicForShuffledInput()
    {
        DiffApplicationEntry gitNew = Application("Git", "2.51.0", "Git.Git");
        DiffApplicationEntry gitOld = Application("Git", "2.50.0", "Git.Git");
        DiffApplicationEntry code = Application("Visual Studio Code", "1.103.0", "Microsoft.VisualStudioCode");
        DiffValueEntry font = Value(DiffArea.Fonts, "Cascadia Code", "Regular");
        DiffValueEntry windows = Value(DiffArea.WindowsInformation, "Edition", "Windows 11 Pro");
        DiffEnvironmentState first = State(
            applications: [gitNew, code, gitOld],
            values: [font, windows],
            paths:
            [
                new DiffPathEntry("C:\\One", "User", 0),
                new DiffPathEntry("C:\\Two", "Machine", 0),
            ]);
        DiffEnvironmentState second = State(
            applications: [gitOld, code, gitNew],
            values: [windows, font],
            paths:
            [
                new DiffPathEntry("C:\\Two", "Machine", 0),
                new DiffPathEntry("C:\\One", "User", 0),
            ]);

        EnvironmentDiffResult expected = _engine.Compare(first, first, DiffRestoreMode.Safe);
        EnvironmentDiffResult actual = _engine.Compare(second, second, DiffRestoreMode.Safe);

        Assert.Equal(expected.Mode, actual.Mode);
        Assert.Equal(expected.Items, actual.Items);
        Assert.Equal(expected.Similarity.OverallScore, actual.Similarity.OverallScore);
        Assert.Equal(expected.Similarity.CoveragePercent, actual.Similarity.CoveragePercent);
        Assert.Equal(expected.Similarity.Categories, actual.Similarity.Categories);
    }

    [Fact]
    public void Compare_CalculatesWeightedSimilarityAndSeparateExclusions()
    {
        DiffEnvironmentState source = State(
            applications: [Application("Git", "2.51.0", "Git.Git")],
            values:
            [
                Value(DiffArea.DevelopmentEnvironment, "dotnet", "10"),
                Value(DiffArea.PluginSettings, "theme", "Dark"),
                Value(DiffArea.EnvironmentVariables, "EDITOR", "code"),
                Value(DiffArea.EnvironmentVariables, "SHELL", "pwsh"),
                Value(DiffArea.Fonts, "Cascadia", "Regular"),
                Value(DiffArea.Fonts, "Unsupported Font", "Regular", isSupported: false),
                Value(
                    DiffArea.EnvironmentVariables,
                    "SECRET",
                    "hidden",
                    isSensitiveExcluded: true),
            ]);
        DiffEnvironmentState target = State(
            applications: [Application("Git", "2.51.0", "Git.Git")],
            values:
            [
                Value(DiffArea.DevelopmentEnvironment, "dotnet", "9"),
                Value(DiffArea.PluginSettings, "theme", "Dark"),
                Value(DiffArea.EnvironmentVariables, "EDITOR", "code"),
                Value(DiffArea.EnvironmentVariables, "SHELL", "cmd"),
                Value(DiffArea.Fonts, "Cascadia", "Regular"),
                Value(DiffArea.Fonts, "Unsupported Font", "Regular", isSupported: false),
                Value(
                    DiffArea.EnvironmentVariables,
                    "SECRET",
                    "hidden",
                    isSensitiveExcluded: true),
            ]);

        EnvironmentSimilarityScore score = _engine
            .Compare(source, target, DiffRestoreMode.Safe)
            .Similarity;

        Assert.Equal(73, score.OverallScore);
        Assert.Equal(100, score.CoveragePercent);
        Assert.Equal(1, score.UnsupportedCount);
        Assert.Equal(1, score.SensitiveExcludedCount);
        Assert.Equal(
            [100, 0, 100, 50, 100],
            score.Categories.Select(category => category.Score));
    }

    [Fact]
    public void Compare_HonorsCancellation()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            _engine.Compare(
                DiffEnvironmentState.Empty,
                DiffEnvironmentState.Empty,
                DiffRestoreMode.Safe,
                cancellation.Token));
    }

    private static DiffApplicationEntry Application(
        string name,
        string version,
        string? wingetId = null,
        string? msixId = null)
    {
        return new DiffApplicationEntry(new ApplicationDescriptor(
            name,
            version,
            "Contoso",
            $"C:\\Apps\\{name}",
            "x64",
            wingetId,
            msixId,
            null));
    }

    private static DiffValueEntry Value(
        DiffArea area,
        string key,
        string? value = null,
        string? hash = null,
        bool isSupported = true,
        bool isSensitiveExcluded = false)
    {
        return new DiffValueEntry(
            area,
            key,
            key,
            value,
            hash,
            isSupported,
            isSensitiveExcluded,
            CanRestoreAutomatically: true);
    }

    private static DiffEnvironmentState State(
        IReadOnlyList<DiffApplicationEntry>? applications = null,
        IReadOnlyList<DiffApplicationEntry>? storeApplications = null,
        IReadOnlyList<DiffValueEntry>? values = null,
        IReadOnlyList<DiffPathEntry>? paths = null)
    {
        return new DiffEnvironmentState(
            applications ?? [],
            storeApplications ?? [],
            values ?? [],
            paths ?? []);
    }
}
