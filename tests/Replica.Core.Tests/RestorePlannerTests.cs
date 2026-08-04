using Replica.Core.Diffing;
using Replica.Core.Planning;

namespace Replica.Core.Tests;

public sealed class RestorePlannerTests
{
    private readonly RestorePlanner _planner = new();

    [Fact]
    public void RestoreActionTypes_AreAllowListedWithoutArbitraryShellCommand()
    {
        Assert.Equal(
            [
                "InstallPackage",
                "UpdatePackage",
                "RestoreFile",
                "MergeJson",
                "SetUserEnvironmentVariable",
                "SetMachineEnvironmentVariable",
                "AddPathEntry",
                "RestoreRegistryValue",
                "InstallExtension",
                "InstallPowerShellModule",
                "RestoreSelectedUserFile",
                "ManualInstruction",
                "Validate",
                "RestartRequired",
            ],
            Enum.GetNames<RestoreActionType>());
    }

    [Fact]
    public void CreatePlan_SafeSelectsOnlySupportedMissingActions()
    {
        EnvironmentDiffResult diff = Diff(
            DiffRestoreMode.Safe,
            Item(DiffType.Missing, DiffArea.Applications, "Git.Git", automatic: true),
            Item(DiffType.VersionMismatch, DiffArea.Applications, "Microsoft.PowerShell", automatic: true),
            Item(DiffType.ValueMismatch, DiffArea.EnvironmentVariables, "EDITOR", automatic: true),
            Item(DiffType.Extra, DiffArea.Applications, "Contoso.Extra", automatic: false));

        RestorePlan plan = _planner.CreatePlan(diff);
        RestoreAction[] primary = PrimaryActions(plan);

        Assert.True(Find(primary, "Git.Git").IsSelected);
        Assert.False(Find(primary, "Microsoft.PowerShell").IsSelected);
        Assert.False(Find(primary, "EDITOR").IsSelected);
        Assert.False(Find(primary, "Contoso.Extra").IsSelected);
        Assert.True(plan.RequiresUserConfirmation);
        Assert.False(plan.IsApproved);
    }

    [Fact]
    public void CreatePlan_RedactsSensitiveValuesAtPlanningBoundary()
    {
        DiffItem sensitive = Item(
            DiffType.SensitiveExcluded,
            DiffArea.EnvironmentVariables,
            "API_TOKEN",
            automatic: false) with
        {
            SourceValue = "snapshot-secret",
            TargetValue = "current-secret",
        };

        RestoreAction action = Assert.Single(
            _planner.CreatePlan(Diff(DiffRestoreMode.Safe, sensitive)).Actions);

        Assert.Equal(RestoreActionType.ManualInstruction, action.Type);
        Assert.Null(action.OriginalValue);
        Assert.Null(action.CurrentValue);
        Assert.Null(action.TargetValue);
        Assert.False(action.IsSelected);
    }

    [Fact]
    public void CreatePlan_RecommendedSelectsCompatibleUpdatesSettingsAndEnvironment()
    {
        EnvironmentDiffResult diff = Diff(
            DiffRestoreMode.Recommended,
            Item(DiffType.Missing, DiffArea.Applications, "Git.Git", automatic: true),
            Item(DiffType.VersionMismatch, DiffArea.Applications, "Microsoft.PowerShell", automatic: true),
            Item(DiffType.FileChanged, DiffArea.ConfigurationFiles, "settings.json", automatic: true),
            Item(DiffType.ValueMismatch, DiffArea.EnvironmentVariables, "EDITOR", automatic: true));
        RestorePlanningOptions options = new(
        [
            new RestoreActionHint(
                DiffArea.Applications,
                "Git.Git",
                EstimatedDuration: TimeSpan.FromMinutes(1),
                EstimatedDownloadBytes: 50_000_000),
        ]);

        RestorePlan plan = _planner.CreatePlan(diff, options);

        Assert.All(PrimaryActions(plan), action => Assert.True(action.IsSelected));
        Assert.Equal(1, plan.DryRun.InstallPackageCount);
        Assert.Equal(1, plan.DryRun.UpdatePackageCount);
        Assert.Equal(1, plan.DryRun.RestoreSettingsCount);
        Assert.Equal(1, plan.DryRun.EnvironmentChangeCount);
        Assert.Equal(50_000_000, plan.DryRun.EstimatedDownloadBytes);
    }

    [Fact]
    public void CreatePlan_ExactShowsManualAndHighRiskActionsWithoutSelectingThem()
    {
        EnvironmentDiffResult diff = Diff(
            DiffRestoreMode.Exact,
            Item(
                DiffType.Missing,
                DiffArea.SelectedUserFiles,
                "project/save.dat",
                automatic: true,
                risk: DiffRiskLevel.High),
            Item(DiffType.Extra, DiffArea.Applications, "Contoso.Extra", automatic: false),
            Item(
                DiffType.VersionMismatch,
                DiffArea.Applications,
                "Git.Git",
                automatic: false,
                reason: "TargetNewerAutomaticDowngradeBlocked"));

        RestorePlan plan = _planner.CreatePlan(diff);
        RestoreAction highRisk = Find(PrimaryActions(plan), "project/save.dat");
        RestoreAction extra = Find(PrimaryActions(plan), "Contoso.Extra");
        RestoreAction downgrade = Find(PrimaryActions(plan), "Git.Git");

        Assert.Equal(RestoreActionType.RestoreSelectedUserFile, highRisk.Type);
        Assert.False(highRisk.IsSelected);
        Assert.Equal(RestoreActionType.ManualInstruction, extra.Type);
        Assert.True(extra.IsManualOnly);
        Assert.Equal(RestoreActionType.ManualInstruction, downgrade.Type);
        Assert.True(downgrade.IsManualOnly);
    }

    [Fact]
    public void CreatePlan_TopologicallySortsDependencyChain()
    {
        EnvironmentDiffResult diff = Diff(
            DiffRestoreMode.Recommended,
            Item(DiffType.Missing, DiffArea.Applications, "Microsoft.VisualStudioCode", automatic: true),
            Item(DiffType.Missing, DiffArea.PluginSettings, "extension:ms-dotnettools.csharp", automatic: true),
            Item(DiffType.FileChanged, DiffArea.ConfigurationFiles, "settings.json", automatic: true));
        RestorePlanningOptions options = new(
        [
            new RestoreActionHint(
                DiffArea.PluginSettings,
                "extension:ms-dotnettools.csharp",
                [new RestoreActionReference(DiffArea.Applications, "Microsoft.VisualStudioCode")]),
            new RestoreActionHint(
                DiffArea.ConfigurationFiles,
                "settings.json",
                [new RestoreActionReference(
                    DiffArea.PluginSettings,
                    "extension:ms-dotnettools.csharp")]),
        ]);

        RestorePlan plan = _planner.CreatePlan(diff, options);
        RestoreAction install = Find(plan.Actions, "Microsoft.VisualStudioCode", RestoreActionType.InstallPackage);
        RestoreAction installValidation = FindValidation(plan, install.Id);
        RestoreAction extension = Find(plan.Actions, "extension:ms-dotnettools.csharp", RestoreActionType.InstallExtension);
        RestoreAction extensionValidation = FindValidation(plan, extension.Id);
        RestoreAction settings = Find(plan.Actions, "settings.json", RestoreActionType.MergeJson);
        RestoreAction settingsValidation = FindValidation(plan, settings.Id);

        AssertOrdered(
            plan.Actions,
            install,
            installValidation,
            extension,
            extensionValidation,
            settings,
            settingsValidation);
    }

    [Fact]
    public void CreatePlan_RejectsDependencyCycle()
    {
        EnvironmentDiffResult diff = Diff(
            DiffRestoreMode.Recommended,
            Item(DiffType.FileChanged, DiffArea.ConfigurationFiles, "one.json", automatic: true),
            Item(DiffType.FileChanged, DiffArea.ConfigurationFiles, "two.json", automatic: true));
        RestorePlanningOptions options = new(
        [
            new RestoreActionHint(
                DiffArea.ConfigurationFiles,
                "one.json",
                [new RestoreActionReference(DiffArea.ConfigurationFiles, "two.json")]),
            new RestoreActionHint(
                DiffArea.ConfigurationFiles,
                "two.json",
                [new RestoreActionReference(DiffArea.ConfigurationFiles, "one.json")]),
        ]);

        RestorePlanningException exception = Assert.Throws<RestorePlanningException>(
            () => _planner.CreatePlan(diff, options));

        Assert.Equal(RestorePlanningFailure.DependencyCycle, exception.Failure);
    }

    [Fact]
    public void CreatePlan_DoesNotPlanReinstallationForExactMatch()
    {
        RestorePlan plan = _planner.CreatePlan(Diff(
            DiffRestoreMode.Exact,
            Item(DiffType.ExactMatch, DiffArea.Applications, "Git.Git", automatic: false)));

        Assert.Empty(plan.Actions);
        Assert.Equal(0, plan.DryRun.InstallPackageCount);
        Assert.Equal(0, plan.DryRun.UpdatePackageCount);
    }

    [Fact]
    public void CreatePlan_BlocksAutomaticDowngrade()
    {
        RestorePlan plan = _planner.CreatePlan(Diff(
            DiffRestoreMode.Exact,
            Item(
                DiffType.VersionMismatch,
                DiffArea.Applications,
                "Git.Git",
                automatic: true,
                reason: "TargetNewerAutomaticDowngradeBlocked")));

        RestoreAction action = Assert.Single(plan.Actions);
        Assert.Equal(RestoreActionType.ManualInstruction, action.Type);
        Assert.False(action.IsSelected);
        Assert.DoesNotContain(plan.Actions, candidate => candidate.Type == RestoreActionType.UpdatePackage);
    }

    [Theory]
    [InlineData(DiffRestoreMode.Safe)]
    [InlineData(DiffRestoreMode.Recommended)]
    [InlineData(DiffRestoreMode.Exact)]
    public void CreatePlan_NeverPlansDeletionForExtraItem(DiffRestoreMode mode)
    {
        RestorePlan plan = _planner.CreatePlan(Diff(
            mode,
            Item(DiffType.Extra, DiffArea.Applications, "Contoso.Extra", automatic: false)));

        RestoreAction action = Assert.Single(plan.Actions);
        Assert.Equal(RestoreActionType.ManualInstruction, action.Type);
        Assert.False(action.IsSelected);
        Assert.Contains("Keep", action.Description, StringComparison.Ordinal);
        Assert.DoesNotContain(
            Enum.GetNames<RestoreActionType>(),
            name => name.Contains("Remove", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Uninstall", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CreatePlan_ClassifiesAdministratorActionsAndRestart()
    {
        RestorePlan plan = _planner.CreatePlan(Diff(
            DiffRestoreMode.Recommended,
            Item(
                DiffType.Missing,
                DiffArea.EnvironmentVariables,
                "JAVA_HOME",
                automatic: true,
                administrator: true,
                restart: true)));

        RestoreAction environment = Find(
            plan.Actions,
            "JAVA_HOME",
            RestoreActionType.SetMachineEnvironmentVariable);
        RestoreAction restart = Assert.Single(
            plan.Actions,
            action => action.Type == RestoreActionType.RestartRequired);

        Assert.True(environment.RequiresAdministrator);
        Assert.True(environment.IsSelected);
        Assert.False(restart.IsSelected);
        Assert.True(restart.IsManualOnly);
        Assert.Equal(1, plan.DryRun.AdministratorActionCount);
        Assert.True(plan.DryRun.RequiresRestart);
    }

    [Fact]
    public void CreatePlan_DryRunIsDeterministicForShuffledInputAndHints()
    {
        DiffItem app = Item(DiffType.Missing, DiffArea.Applications, "Git.Git", automatic: true);
        DiffItem setting = Item(DiffType.FileChanged, DiffArea.ConfigurationFiles, "settings.json", automatic: true);
        DiffItem environment = Item(DiffType.ValueMismatch, DiffArea.EnvironmentVariables, "EDITOR", automatic: true);
        RestoreActionHint appHint = new(
            DiffArea.Applications,
            "Git.Git",
            EstimatedDuration: TimeSpan.FromMinutes(2),
            EstimatedDownloadBytes: 42_000_000);
        RestoreActionHint settingHint = new(
            DiffArea.ConfigurationFiles,
            "settings.json",
            [new RestoreActionReference(DiffArea.Applications, "Git.Git")]);

        RestorePlan expected = _planner.CreatePlan(
            Diff(DiffRestoreMode.Recommended, app, setting, environment),
            new RestorePlanningOptions([appHint, settingHint]));
        RestorePlan actual = _planner.CreatePlan(
            Diff(DiffRestoreMode.Recommended, environment, setting, app),
            new RestorePlanningOptions([settingHint, appHint]));

        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(
            expected.Actions.Select(ActionFingerprint),
            actual.Actions.Select(ActionFingerprint));
        Assert.Equal(expected.DryRun, actual.DryRun);
    }

    [Fact]
    public void Approve_RequiresExplicitReviewAndPreservesTypedPlan()
    {
        RestorePlan pending = _planner.CreatePlan(Diff(
            DiffRestoreMode.Safe,
            Item(DiffType.Missing, DiffArea.Applications, "Git.Git", automatic: true)));

        RestorePlan approved = pending.Approve();

        Assert.True(pending.RequiresUserConfirmation);
        Assert.False(pending.IsApproved);
        Assert.False(approved.RequiresUserConfirmation);
        Assert.True(approved.IsApproved);
        Assert.Equal(pending.Actions, approved.Actions);
    }

    [Fact]
    public void CreatePlan_HonorsCancellation()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => _planner.CreatePlan(
            Diff(
                DiffRestoreMode.Safe,
                Item(DiffType.Missing, DiffArea.Applications, "Git.Git", automatic: true)),
            cancellationToken: cancellation.Token));
    }

    private static EnvironmentDiffResult Diff(
        DiffRestoreMode mode,
        params DiffItem[] items)
    {
        return new EnvironmentDiffResult(
            mode,
            items,
            new EnvironmentSimilarityScore(null, 0, 0, 0, 0, []));
    }

    private static DiffItem Item(
        DiffType type,
        DiffArea area,
        string key,
        bool automatic,
        DiffRiskLevel risk = DiffRiskLevel.Low,
        bool administrator = false,
        bool restart = false,
        string? reason = null)
    {
        return new DiffItem(
            type,
            area,
            key,
            key,
            "snapshot-value",
            "current-value",
            null,
            automatic,
            type == DiffType.Extra,
            false,
            risk,
            administrator,
            restart,
            type == DiffType.ExactMatch ? 100 : 0,
            reason ?? type.ToString());
    }

    private static RestoreAction[] PrimaryActions(RestorePlan plan)
    {
        return plan.Actions
            .Where(action => action.Type is not (
                RestoreActionType.Validate or RestoreActionType.RestartRequired))
            .ToArray();
    }

    private static RestoreAction Find(
        IEnumerable<RestoreAction> actions,
        string diffKey)
    {
        return Assert.Single(actions, action => action.SourceDiffKey == diffKey);
    }

    private static RestoreAction Find(
        IEnumerable<RestoreAction> actions,
        string diffKey,
        RestoreActionType type)
    {
        return Assert.Single(
            actions,
            action => action.SourceDiffKey == diffKey && action.Type == type);
    }

    private static RestoreAction FindValidation(RestorePlan plan, string dependencyId)
    {
        return Assert.Single(
            plan.Actions,
            action => action.Type == RestoreActionType.Validate &&
                action.Dependencies.SequenceEqual([dependencyId], StringComparer.Ordinal));
    }

    private static void AssertOrdered(
        IReadOnlyList<RestoreAction> actions,
        params RestoreAction[] expected)
    {
        RestoreAction[] actionArray = actions.ToArray();
        int previous = -1;
        foreach (RestoreAction action in expected)
        {
            int index = Array.IndexOf(actionArray, action);
            Assert.True(index > previous, $"Action '{action.Id}' was not topologically ordered.");
            previous = index;
        }
    }

    private static string ActionFingerprint(RestoreAction action)
    {
        return string.Join(
            '|',
            action.Id,
            action.Type,
            action.Name,
            action.Description,
            action.OriginalValue,
            action.CurrentValue,
            action.TargetValue,
            action.Risk,
            action.RequiresAdministrator,
            action.RequiresRestart,
            action.CanRollback,
            string.Join(',', action.Dependencies),
            action.EstimatedDuration.Ticks,
            action.EstimatedDownloadBytes,
            action.IsSelected,
            action.IsManualOnly,
            action.SourceArea,
            action.SourceDiffKey,
            action.ReasonCode);
    }
}
