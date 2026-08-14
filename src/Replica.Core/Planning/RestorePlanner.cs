using System.Security.Cryptography;
using System.Text;
using Replica.Core.Diffing;
using Replica.Core.Services;

namespace Replica.Core.Planning;

public sealed class RestorePlanner : IRestorePlanner
{
    private const long MaximumEstimatedDownloadBytes = 17_592_186_044_416;
    private static readonly TimeSpan MaximumEstimatedDuration = TimeSpan.FromDays(30);
    private static readonly TimeSpan ValidationDuration = TimeSpan.FromSeconds(5);

    public RestorePlan CreatePlan(
        EnvironmentDiffResult diff,
        RestorePlanningOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ValidateDiff(diff);
        options ??= RestorePlanningOptions.Empty;
        Dictionary<string, RestoreActionHint> hints = ValidateHints(options.Hints);
        List<ActionPair> pairs = [];

        foreach (DiffItem item in diff.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Type == DiffType.ExactMatch)
            {
                continue;
            }

            string referenceKey = ReferenceKey(item.Area, item.Key);
            hints.TryGetValue(referenceKey, out RestoreActionHint? hint);
            ActionClassification classification = Classify(item);
            if (!classification.IsManualOnly &&
                options.SupportedAutomaticActionTypes is not null &&
                !options.SupportedAutomaticActionTypes.Contains(classification.Type))
            {
                classification = ActionClassification.Manual(
                    "This difference requires a reviewed manual action because no allow-listed execution handler is available.");
            }

            RestoreAction primary = CreatePrimaryAction(item, diff.Mode, classification, hint);
            RestoreAction? validation = classification.IsManualOnly
                ? null
                : CreateValidationAction(primary);
            pairs.Add(new ActionPair(item, primary, validation, hint));
        }

        Dictionary<string, ActionPair> pairsByReference = new(StringComparer.Ordinal);
        foreach (ActionPair pair in pairs)
        {
            string key = ReferenceKey(pair.Item.Area, pair.Item.Key);
            if (!pairsByReference.TryAdd(key, pair))
            {
                throw new RestorePlanningException(
                    RestorePlanningFailure.DuplicateAction,
                    $"Duplicate diff item reference '{pair.Item.Area}:{pair.Item.Key}'.");
            }
        }

        string? unusedHint = hints.Keys.FirstOrDefault(key => !pairsByReference.ContainsKey(key));
        if (unusedHint is not null)
        {
            throw new RestorePlanningException(
                RestorePlanningFailure.InvalidInput,
                $"Restore planning hint '{unusedHint}' does not match a restorable diff item.");
        }
        List<RestoreAction> actions = [];
        foreach (ActionPair pair in pairs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RestoreAction primary = ApplyDependencies(pair, pairsByReference);
            RestoreAction? validation = pair.Validation;
            if (validation is not null)
            {
                validation = validation with
                {
                    Dependencies = [primary.Id],
                    IsSelected = primary.IsSelected,
                };
            }

            actions.Add(primary);
            if (validation is not null)
            {
                actions.Add(validation);
            }
        }

        AddRestartAction(actions, pairs);
        RestoreAction[] ordered = ApplySelectionClosure(
            TopologicalSort(actions, cancellationToken));
        RestoreDryRunSummary summary = CreateDryRunSummary(ordered);
        return new RestorePlan(
            CreateStableId(
                "plan",
                diff.Mode.ToString(),
                string.Join('|', ordered.Select(ActionFingerprint))),
            diff.Mode,
            ordered,
            summary,
            RestorePlanReviewStatus.PendingReview,
            DateTimeOffset.UtcNow);
    }

    private static RestoreAction CreatePrimaryAction(
        DiffItem item,
        DiffRestoreMode mode,
        ActionClassification classification,
        RestoreActionHint? hint)
    {
        string id = CreateStableId(
            "action",
            item.Area.ToString(),
            item.Key,
            classification.Type.ToString());
        TimeSpan duration = hint?.EstimatedDuration ?? classification.EstimatedDuration;
        long downloadBytes = hint?.EstimatedDownloadBytes ?? 0;
        bool canRollback = hint?.CanRollback ?? classification.CanRollback;
        bool selected = IsDefaultSelected(item, mode, classification);
        bool redactValues = item.Type == DiffType.SensitiveExcluded;
        return new RestoreAction(
            id,
            classification.Type,
            item.DisplayName,
            classification.Description,
            redactValues ? null : item.SourceValue,
            redactValues ? null : item.TargetValue,
            redactValues ? null : item.SourceValue,
            item.Risk,
            item.RequiresAdministrator,
            item.RequiresRestart,
            canRollback,
            [],
            duration,
            downloadBytes,
            selected,
            classification.IsManualOnly,
            item.Area,
            item.Key,
            item.ReasonCode,
            item.MatchConfidence);
    }

    private static RestoreAction CreateValidationAction(RestoreAction primary)
    {
        return new RestoreAction(
            CreateStableId("validate", primary.Id),
            RestoreActionType.Validate,
            $"Validate {primary.Name}",
            "Rescan and verify the planned target state after this action.",
            null,
            null,
            primary.TargetValue,
            DiffRiskLevel.None,
            false,
            false,
            false,
            [primary.Id],
            ValidationDuration,
            0,
            primary.IsSelected,
            false,
            primary.SourceArea,
            primary.SourceDiffKey,
            "ValidatePlannedAction");
    }

    private static RestoreAction ApplyDependencies(
        ActionPair pair,
        IReadOnlyDictionary<string, ActionPair> pairsByReference)
    {
        if (pair.Hint?.DependsOn is not { Count: > 0 } dependencies)
        {
            return pair.Primary;
        }

        List<string> ids = [];
        foreach (RestoreActionReference dependency in dependencies)
        {
            string key = ReferenceKey(dependency.Area, dependency.DiffKey);
            if (!pairsByReference.TryGetValue(key, out ActionPair? dependencyPair))
            {
                throw new RestorePlanningException(
                    RestorePlanningFailure.MissingDependency,
                    $"Restore dependency '{dependency.Area}:{dependency.DiffKey}' does not exist.");
            }

            ids.Add(dependencyPair.Validation?.Id ?? dependencyPair.Primary.Id);
        }

        return pair.Primary with
        {
            Dependencies = ids.Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray(),
        };
    }

    private static void AddRestartAction(
        ICollection<RestoreAction> actions,
        IReadOnlyList<ActionPair> pairs)
    {
        ActionPair[] requiringRestart = pairs
            .Where(pair => !pair.Primary.IsManualOnly && pair.Primary.RequiresRestart)
            .ToArray();
        if (requiringRestart.Length == 0)
        {
            return;
        }

        string[] dependencies = requiringRestart
            .Select(pair => pair.Validation?.Id ?? pair.Primary.Id)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        actions.Add(new RestoreAction(
            CreateStableId("restart", string.Join('|', dependencies)),
            RestoreActionType.RestartRequired,
            "Restart Windows",
            "A restart is required after the selected restore actions complete.",
            null,
            null,
            null,
            DiffRiskLevel.Medium,
            false,
            true,
            false,
            dependencies,
            TimeSpan.Zero,
            0,
            false,
            true,
            DiffArea.WindowsInformation,
            "RESTART",
            "RestartRequiredAfterRestore"));
    }

    private static ActionClassification Classify(DiffItem item)
    {
        if (RequiresManualInstruction(item))
        {
            return ActionClassification.Manual(ManualDescription(item));
        }

        return item.Area switch
        {
            DiffArea.Applications or DiffArea.StoreApplications => item.Type switch
            {
                DiffType.Missing => new(
                    RestoreActionType.InstallPackage,
                    "Install the missing package using its validated package identity.",
                    false,
                    false,
                    TimeSpan.FromMinutes(2)),
                DiffType.VersionMismatch => new(
                    RestoreActionType.UpdatePackage,
                    "Update the package to the compatible snapshot version.",
                    false,
                    false,
                    TimeSpan.FromMinutes(2)),
                _ => ActionClassification.Manual(ManualDescription(item)),
            },
            DiffArea.EnvironmentVariables => new(
                item.RequiresAdministrator
                    ? RestoreActionType.SetMachineEnvironmentVariable
                    : RestoreActionType.SetUserEnvironmentVariable,
                "Merge the environment variable without replacing unrelated values.",
                true,
                false,
                TimeSpan.FromSeconds(10)),
            DiffArea.Path when item.Type == DiffType.Missing => new(
                RestoreActionType.AddPathEntry,
                "Add the missing PATH entry while preserving existing entries and order.",
                true,
                false,
                TimeSpan.FromSeconds(10)),
            DiffArea.Path => ActionClassification.Manual(
                "Review the PATH ordering difference; automatic whole-PATH replacement is blocked."),
            DiffArea.ConfigurationFiles => new(
                IsJson(item.Key) ? RestoreActionType.MergeJson : RestoreActionType.RestoreFile,
                IsJson(item.Key)
                    ? "Merge supported JSON settings using a format-aware handler."
                    : "Restore the approved configuration file using a journaled atomic write.",
                true,
                false,
                TimeSpan.FromSeconds(20)),
            DiffArea.SelectedUserFiles => new(
                RestoreActionType.RestoreSelectedUserFile,
                "Restore the explicitly selected user file after destination and checksum validation.",
                true,
                false,
                TimeSpan.FromSeconds(30)),
            DiffArea.PluginSettings when HasPrefix(item.Key, "registry:") => new(
                RestoreActionType.RestoreRegistryValue,
                "Restore an allow-listed registry value through its built-in plugin.",
                true,
                false,
                TimeSpan.FromSeconds(10)),
            DiffArea.PluginSettings when HasPrefix(item.Key, "extension:") => new(
                RestoreActionType.InstallExtension,
                "Install the allow-listed application extension.",
                false,
                false,
                TimeSpan.FromSeconds(30)),
            DiffArea.PluginSettings when HasPrefix(item.Key, "powershell-module:") => new(
                RestoreActionType.InstallPowerShellModule,
                "Install the allow-listed PowerShell module without executing arbitrary commands.",
                false,
                false,
                TimeSpan.FromMinutes(1)),
            DiffArea.PluginSettings => new(
                IsJson(item.Key) ? RestoreActionType.MergeJson : RestoreActionType.RestoreFile,
                IsJson(item.Key)
                    ? "Merge the supported plugin JSON settings."
                    : "Restore the supported plugin settings file.",
                true,
                false,
                TimeSpan.FromSeconds(20)),
            DiffArea.DevelopmentEnvironment when HasPrefix(item.Key, "extension:") => new(
                RestoreActionType.InstallExtension,
                "Install the allow-listed development-tool extension.",
                false,
                false,
                TimeSpan.FromSeconds(30)),
            DiffArea.DevelopmentEnvironment when HasPrefix(item.Key, "powershell-module:") => new(
                RestoreActionType.InstallPowerShellModule,
                "Install the allow-listed PowerShell module.",
                false,
                false,
                TimeSpan.FromMinutes(1)),
            _ => ActionClassification.Manual(ManualDescription(item)),
        };
    }

    private static bool RequiresManualInstruction(DiffItem item)
    {
        return item.Type is
                DiffType.Extra or
                DiffType.Conflict or
                DiffType.Unsupported or
                DiffType.SensitiveExcluded or
                DiffType.ManualActionRequired or
                DiffType.Error ||
            item.ReasonCode.Equals(
                "TargetNewerAutomaticDowngradeBlocked",
                StringComparison.Ordinal);
    }

    private static bool IsDefaultSelected(
        DiffItem item,
        DiffRestoreMode mode,
        ActionClassification classification)
    {
        if (classification.IsManualOnly ||
            item.Risk is DiffRiskLevel.High or DiffRiskLevel.Critical ||
            !item.CanAutomaticallyRestore)
        {
            return false;
        }

        return mode switch
        {
            DiffRestoreMode.Safe => item.Type == DiffType.Missing,
            DiffRestoreMode.Recommended => item.Type is
                DiffType.Missing or
                DiffType.VersionMismatch or
                DiffType.ValueMismatch or
                DiffType.FileChanged,
            DiffRestoreMode.Exact => item.Type is not DiffType.Extra,
            _ => false,
        };
    }

    private static RestoreAction[] TopologicalSort(
        IReadOnlyCollection<RestoreAction> actions,
        CancellationToken cancellationToken)
    {
        Dictionary<string, RestoreAction> byId = new(StringComparer.Ordinal);
        foreach (RestoreAction action in actions)
        {
            if (!byId.TryAdd(action.Id, action))
            {
                throw new RestorePlanningException(
                    RestorePlanningFailure.DuplicateAction,
                    $"Duplicate restore action identifier '{action.Id}'.");
            }
        }

        Dictionary<string, int> indegree = byId.Keys.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        Dictionary<string, List<string>> dependents = byId.Keys.ToDictionary(
            id => id,
            _ => new List<string>(),
            StringComparer.Ordinal);
        foreach (RestoreAction action in byId.Values)
        {
            foreach (string dependency in action.Dependencies)
            {
                if (!byId.ContainsKey(dependency))
                {
                    throw new RestorePlanningException(
                        RestorePlanningFailure.MissingDependency,
                        $"Restore action '{action.Id}' depends on missing action '{dependency}'.");
                }

                indegree[action.Id]++;
                dependents[dependency].Add(action.Id);
            }
        }

        SortedSet<string> ready = new(new ActionIdComparer(byId));
        foreach ((string id, int count) in indegree)
        {
            if (count == 0)
            {
                ready.Add(id);
            }
        }

        List<RestoreAction> ordered = [];
        while (ready.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string id = ready.Min!;
            ready.Remove(id);
            ordered.Add(byId[id]);
            foreach (string dependent in dependents[id].OrderBy(value => value, StringComparer.Ordinal))
            {
                indegree[dependent]--;
                if (indegree[dependent] == 0)
                {
                    ready.Add(dependent);
                }
            }
        }

        if (ordered.Count != actions.Count)
        {
            throw new RestorePlanningException(
                RestorePlanningFailure.DependencyCycle,
                "The restore plan dependency graph contains a cycle.");
        }

        return ordered.ToArray();
    }

    private static RestoreAction[] ApplySelectionClosure(
        IReadOnlyList<RestoreAction> ordered)
    {
        HashSet<string> selected = new(StringComparer.Ordinal);
        RestoreAction[] result = new RestoreAction[ordered.Count];
        for (int index = 0; index < ordered.Count; index++)
        {
            RestoreAction action = ordered[index];
            bool isSelected = action.IsSelected &&
                action.Dependencies.All(selected.Contains);
            RestoreAction updated = action with { IsSelected = isSelected };
            result[index] = updated;
            if (isSelected)
            {
                selected.Add(updated.Id);
            }
        }

        return result;
    }

    private static RestoreDryRunSummary CreateDryRunSummary(IReadOnlyList<RestoreAction> actions)
    {
        RestoreAction[] selected = actions.Where(action => action.IsSelected).ToArray();
        return new RestoreDryRunSummary(
            CountSelectedPrimary(selected, RestoreActionType.InstallPackage),
            CountSelectedPrimary(selected, RestoreActionType.UpdatePackage),
            selected.Count(action => action.Type is
                RestoreActionType.RestoreFile or
                RestoreActionType.MergeJson or
                RestoreActionType.RestoreRegistryValue or
                RestoreActionType.InstallExtension or
                RestoreActionType.InstallPowerShellModule),
            CountSelectedPrimary(selected, RestoreActionType.RestoreSelectedUserFile),
            selected.Count(action => action.Type is
                RestoreActionType.SetUserEnvironmentVariable or
                RestoreActionType.SetMachineEnvironmentVariable or
                RestoreActionType.AddPathEntry),
            selected.Count(action => action.RequiresAdministrator),
            selected.Any(action => action.RequiresRestart),
            selected.Count(action => action.CanRollback),
            actions.Count(action => action.IsManualOnly),
            selected.Length,
            TimeSpan.FromTicks(selected.Sum(action => action.EstimatedDuration.Ticks)),
            selected.Sum(action => action.EstimatedDownloadBytes));
    }

    private static int CountSelectedPrimary(
        IEnumerable<RestoreAction> actions,
        RestoreActionType type)
    {
        return actions.Count(action => action.Type == type);
    }

    private static Dictionary<string, RestoreActionHint> ValidateHints(
        IReadOnlyList<RestoreActionHint> hints)
    {
        if (hints is null || hints.Any(hint =>
                hint is null ||
                !Enum.IsDefined(hint.Area) ||
                string.IsNullOrWhiteSpace(hint.DiffKey) ||
                hint.EstimatedDuration is { } duration &&
                    (duration < TimeSpan.Zero || duration > MaximumEstimatedDuration) ||
                hint.EstimatedDownloadBytes is < 0 or > MaximumEstimatedDownloadBytes ||
                hint.DependsOn?.Any(dependency =>
                    dependency is null ||
                    !Enum.IsDefined(dependency.Area) ||
                    string.IsNullOrWhiteSpace(dependency.DiffKey)) == true))
        {
            throw new RestorePlanningException(
                RestorePlanningFailure.InvalidInput,
                "Restore planning hints are invalid.");
        }

        Dictionary<string, RestoreActionHint> result = new(StringComparer.Ordinal);
        foreach (RestoreActionHint hint in hints)
        {
            if (!result.TryAdd(ReferenceKey(hint.Area, hint.DiffKey), hint))
            {
                throw new RestorePlanningException(
                    RestorePlanningFailure.DuplicateHint,
                    $"Duplicate restore planning hint for '{hint.Area}:{hint.DiffKey}'.");
            }
        }

        return result;
    }

    private static void ValidateDiff(EnvironmentDiffResult diff)
    {
        if (diff is null || diff.Items is null || diff.Similarity is null || !Enum.IsDefined(diff.Mode) ||
            diff.Items.Any(item =>
                item is null ||
                !Enum.IsDefined(item.Type) ||
                !Enum.IsDefined(item.Area) ||
                !Enum.IsDefined(item.Risk) ||
                string.IsNullOrWhiteSpace(item.Key) ||
                string.IsNullOrWhiteSpace(item.DisplayName) ||
                string.IsNullOrWhiteSpace(item.ReasonCode)))
        {
            throw new RestorePlanningException(
                RestorePlanningFailure.InvalidInput,
                "The environment diff is invalid for restore planning.");
        }
    }

    private static string ReferenceKey(DiffArea area, string key)
    {
        return $"{(int)area:D2}:{key.Trim().ToUpperInvariant()}";
    }

    private static string CreateStableId(string prefix, params string[] parts)
    {
        string source = string.Join('\u001F', parts.Select(part => part.Trim()));
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
        return $"{prefix}-{digest[..16].ToLowerInvariant()}";
    }

    private static string ActionFingerprint(RestoreAction action)
    {
        return string.Join(
            '\u001E',
            action.Id,
            action.Type,
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
            action.MatchConfidence,
            action.ReasonCode);
    }

    private static bool IsJson(string key)
    {
        return key.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasPrefix(string key, string prefix)
    {
        return key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string ManualDescription(DiffItem item)
    {
        return item.Type switch
        {
            DiffType.Extra => "Keep the current-only item. Automatic removal is not supported.",
            DiffType.Conflict => "Resolve this conflict explicitly before a restore action can be planned.",
            DiffType.Unsupported => "Use the documented manual recovery procedure for this unsupported item.",
            DiffType.SensitiveExcluded => "The sensitive value was excluded and cannot be restored automatically.",
            DiffType.Error => "Resolve the discovery error and generate a new diff before restoring this item.",
            _ when item.ReasonCode.Equals(
                "TargetNewerAutomaticDowngradeBlocked",
                StringComparison.Ordinal) =>
                "Keep the newer target version; automatic downgrade is blocked.",
            _ => "Review and perform this item manually using its supported product workflow.",
        };
    }

    private sealed record ActionClassification(
        RestoreActionType Type,
        string Description,
        bool CanRollback,
        bool IsManualOnly,
        TimeSpan EstimatedDuration)
    {
        public static ActionClassification Manual(string description)
        {
            return new ActionClassification(
                RestoreActionType.ManualInstruction,
                description,
                false,
                true,
                TimeSpan.Zero);
        }
    }

    private sealed record ActionPair(
        DiffItem Item,
        RestoreAction Primary,
        RestoreAction? Validation,
        RestoreActionHint? Hint);

    private sealed class ActionIdComparer : IComparer<string>
    {
        private readonly IReadOnlyDictionary<string, RestoreAction> _actions;

        public ActionIdComparer(IReadOnlyDictionary<string, RestoreAction> actions)
        {
            _actions = actions;
        }

        public int Compare(string? first, string? second)
        {
            if (ReferenceEquals(first, second))
            {
                return 0;
            }

            if (first is null)
            {
                return -1;
            }

            if (second is null)
            {
                return 1;
            }

            RestoreAction firstAction = _actions[first];
            RestoreAction secondAction = _actions[second];
            int type = firstAction.Type.CompareTo(secondAction.Type);
            if (type != 0)
            {
                return type;
            }

            int name = StringComparer.OrdinalIgnoreCase.Compare(firstAction.Name, secondAction.Name);
            if (name != 0)
            {
                return name;
            }

            return StringComparer.Ordinal.Compare(first, second);
        }
    }
}
