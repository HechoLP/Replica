using Replica.Core.Matching;
using Replica.Core.Services;

namespace Replica.Core.Diffing;

public sealed class EnvironmentDiffEngine : IEnvironmentDiffEngine
{
    private static readonly IReadOnlyDictionary<DiffScoreCategory, int> CategoryWeights =
        new Dictionary<DiffScoreCategory, int>
        {
            [DiffScoreCategory.Applications] = 30,
            [DiffScoreCategory.DevelopmentEnvironment] = 20,
            [DiffScoreCategory.ApplicationSettings] = 25,
            [DiffScoreCategory.EnvironmentAndPath] = 15,
            [DiffScoreCategory.FontsAndOther] = 10,
        };

    private readonly IApplicationAutomationPolicy _automationPolicy;
    private readonly IApplicationMatcher _applicationMatcher;
    private readonly IApplicationVersionComparer _versionComparer;

    public EnvironmentDiffEngine(
        IApplicationMatcher applicationMatcher,
        IApplicationVersionComparer versionComparer,
        IApplicationAutomationPolicy automationPolicy)
    {
        _applicationMatcher = applicationMatcher;
        _versionComparer = versionComparer;
        _automationPolicy = automationPolicy;
    }

    public EnvironmentDiffResult Compare(
        DiffEnvironmentState source,
        DiffEnvironmentState target,
        DiffRestoreMode mode,
        CancellationToken cancellationToken = default)
    {
        ValidateState(source, nameof(source));
        ValidateState(target, nameof(target));
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
        }

        List<DiffItem> items = [];
        CompareApplications(
            source.Applications,
            target.Applications,
            DiffArea.Applications,
            mode,
            items,
            cancellationToken);
        CompareApplications(
            source.StoreApplications,
            target.StoreApplications,
            DiffArea.StoreApplications,
            mode,
            items,
            cancellationToken);
        CompareValues(source.Values, target.Values, mode, items, cancellationToken);
        ComparePaths(source.PathEntries, target.PathEntries, mode, items, cancellationToken);

        DiffItem[] orderedItems = items
            .OrderBy(item => item.Area)
            .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Type)
            .ToArray();
        return new EnvironmentDiffResult(mode, orderedItems, CalculateScore(orderedItems));
    }

    private void CompareApplications(
        IReadOnlyList<DiffApplicationEntry> source,
        IReadOnlyList<DiffApplicationEntry> target,
        DiffArea area,
        DiffRestoreMode mode,
        ICollection<DiffItem> items,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ApplicationMatchingResult matching = _applicationMatcher.Match(
            source.Select(entry => entry.Application).ToArray(),
            target.Select(entry => entry.Application).ToArray());

        foreach (ApplicationMatch match in matching.Matches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplicationPolicy sourcePolicy = GetApplicationPolicy(source, match.Source);
            ApplicationPolicy targetPolicy = GetApplicationPolicy(target, match.Target);
            ApplicationPolicy combinedPolicy = ApplicationPolicy.Combine(sourcePolicy, targetPolicy);
            string key = GetApplicationKey(match.Source.Application);

            if (!combinedPolicy.IsSupported)
            {
                items.Add(CreateApplicationItem(
                    DiffType.Unsupported,
                    area,
                    key,
                    match,
                    combinedPolicy,
                    false,
                    0,
                    "ApplicationUnsupported"));
                continue;
            }

            if (!match.AllowsAutomaticAction)
            {
                items.Add(CreateApplicationItem(
                    DiffType.ManualActionRequired,
                    area,
                    key,
                    match,
                    combinedPolicy,
                    false,
                    0,
                    "ApplicationIdentityRequiresReview"));
                continue;
            }

            string? sourceVersion = match.Source.Application.Version;
            string? targetVersion = match.Target.Application.Version;
            if (string.IsNullOrWhiteSpace(sourceVersion) &&
                string.IsNullOrWhiteSpace(targetVersion))
            {
                items.Add(CreateApplicationItem(
                    DiffType.ExactMatch,
                    area,
                    key,
                    match,
                    combinedPolicy,
                    false,
                    100,
                    "ApplicationIdentityMatchWithoutVersion"));
                continue;
            }

            ApplicationVersionComparisonResult version = _versionComparer.Compare(
                sourceVersion,
                targetVersion);
            switch (version.Comparison)
            {
                case ApplicationVersionComparison.Equal:
                    items.Add(CreateApplicationItem(
                        DiffType.ExactMatch,
                        area,
                        key,
                        match,
                        combinedPolicy,
                        false,
                        100,
                        "ApplicationAndVersionMatch"));
                    break;
                case ApplicationVersionComparison.SourceNewer:
                    {
                        ApplicationAutomationDecision decision = _automationPolicy.Evaluate(
                            match.Confidence,
                            version.Comparison);
                        bool automatic = mode is not DiffRestoreMode.Safe &&
                            combinedPolicy.CanRestoreAutomatically &&
                            decision.CanAutomaticallyUpdate &&
                            IsRiskAutomaticallyAllowed(combinedPolicy.Risk);
                        items.Add(CreateApplicationItem(
                            DiffType.VersionMismatch,
                            area,
                            key,
                            match,
                            combinedPolicy,
                            automatic,
                            50,
                            automatic ? "SourceVersionCanUpdate" : "SourceVersionRequiresReview"));
                        break;
                    }
                case ApplicationVersionComparison.TargetNewer:
                    items.Add(CreateApplicationItem(
                        DiffType.VersionMismatch,
                        area,
                        key,
                        match,
                        combinedPolicy,
                        false,
                        50,
                        "TargetNewerAutomaticDowngradeBlocked"));
                    break;
                default:
                    items.Add(CreateApplicationItem(
                        DiffType.ManualActionRequired,
                        area,
                        key,
                        match,
                        combinedPolicy,
                        false,
                        0,
                        "ApplicationVersionRequiresReview"));
                    break;
            }
        }

        HashSet<ConsolidatedApplication> ambiguousTargets = matching.Ambiguous
            .SelectMany(ambiguity => ambiguity.Candidates)
            .ToHashSet();
        foreach (ApplicationMatchAmbiguity ambiguity in matching.Ambiguous)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplicationPolicy policy = GetApplicationPolicy(source, ambiguity.Source);
            items.Add(new DiffItem(
                DiffType.Conflict,
                area,
                GetApplicationKey(ambiguity.Source.Application),
                ambiguity.Source.Application.DisplayName,
                ambiguity.Source.Application.Version,
                string.Join(
                    ", ",
                    ambiguity.Candidates.Select(candidate => candidate.Application.DisplayName)),
                ambiguity.Confidence,
                false,
                false,
                false,
                MaxRisk(policy.Risk, DiffRiskLevel.High),
                policy.RequiresAdministrator,
                policy.RequiresRestart,
                0,
                "AmbiguousApplicationMatch"));
        }

        foreach (ConsolidatedApplication missing in matching.UnmatchedSource)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplicationPolicy policy = GetApplicationPolicy(source, missing);
            ApplicationMatchConfidence confidence = _applicationMatcher
                .AssessIdentityConfidence(missing.Application);
            if (!policy.IsSupported)
            {
                items.Add(CreateUnmatchedApplicationItem(
                    DiffType.Unsupported,
                    area,
                    missing,
                    policy,
                    confidence,
                    false,
                    false,
                    0,
                    "MissingApplicationUnsupported"));
                continue;
            }

            ApplicationAutomationDecision decision = _automationPolicy.Evaluate(
                confidence,
                ApplicationVersionComparison.Unknown);
            bool automatic = policy.CanRestoreAutomatically &&
                decision.CanAutomaticallyInstall &&
                IsRiskAutomaticallyAllowed(policy.Risk);
            items.Add(CreateUnmatchedApplicationItem(
                DiffType.Missing,
                area,
                missing,
                policy,
                confidence,
                automatic,
                false,
                0,
                automatic ? "MissingApplicationCanInstall" : "MissingApplicationRequiresReview"));
        }

        foreach (ConsolidatedApplication extra in matching.UnmatchedTarget)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ambiguousTargets.Contains(extra))
            {
                continue;
            }

            ApplicationPolicy policy = GetApplicationPolicy(target, extra);
            items.Add(CreateUnmatchedApplicationItem(
                DiffType.Extra,
                area,
                extra,
                policy with { Risk = MaxRisk(policy.Risk, DiffRiskLevel.High) },
                _applicationMatcher.AssessIdentityConfidence(extra.Application),
                false,
                true,
                0,
                mode == DiffRestoreMode.Exact
                    ? "ExtraPreservedInExactMode"
                    : "ExtraPreservedNoAutomaticRemoval"));
        }
    }

    private static void CompareValues(
        IReadOnlyList<DiffValueEntry> source,
        IReadOnlyList<DiffValueEntry> target,
        DiffRestoreMode mode,
        ICollection<DiffItem> items,
        CancellationToken cancellationToken)
    {
        Dictionary<string, List<DiffValueEntry>> sourceGroups = GroupValues(source);
        Dictionary<string, List<DiffValueEntry>> targetGroups = GroupValues(target);
        string[] keys = sourceGroups.Keys
            .Concat(targetGroups.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

        foreach (string key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sourceGroups.TryGetValue(key, out List<DiffValueEntry>? sourceEntries);
            targetGroups.TryGetValue(key, out List<DiffValueEntry>? targetEntries);
            sourceEntries ??= [];
            targetEntries ??= [];
            DiffValueEntry? sourceEntry = sourceEntries.FirstOrDefault();
            DiffValueEntry? targetEntry = targetEntries.FirstOrDefault();
            DiffValueEntry representative = sourceEntry ?? targetEntry!;
            DiffRiskLevel risk = MaxRisk(
                sourceEntry?.Risk ?? DiffRiskLevel.None,
                targetEntry?.Risk ?? DiffRiskLevel.None);
            bool requiresAdministrator = sourceEntries.Concat(targetEntries)
                .Any(entry => entry.RequiresAdministrator);
            bool requiresRestart = sourceEntries.Concat(targetEntries)
                .Any(entry => entry.RequiresRestart);

            if (sourceEntries.Concat(targetEntries).Any(entry => entry.IsSensitiveExcluded))
            {
                items.Add(CreateValueItem(
                    DiffType.SensitiveExcluded,
                    representative,
                    null,
                    null,
                    false,
                    false,
                    risk,
                    requiresAdministrator,
                    requiresRestart,
                    0,
                    "SensitiveValueExcluded"));
                continue;
            }

            if (sourceEntries.Count > 1 || targetEntries.Count > 1 ||
                sourceEntries.Concat(targetEntries).Any(entry => entry.IsConflict))
            {
                items.Add(CreateValueItem(
                    DiffType.Conflict,
                    representative,
                    sourceEntry,
                    targetEntry,
                    false,
                    false,
                    risk,
                    requiresAdministrator,
                    requiresRestart,
                    0,
                    "DuplicateOrConflictingValue"));
                continue;
            }

            if (sourceEntries.Concat(targetEntries).Any(entry => !entry.IsSupported))
            {
                items.Add(CreateValueItem(
                    DiffType.Unsupported,
                    representative,
                    sourceEntry,
                    targetEntry,
                    false,
                    false,
                    risk,
                    requiresAdministrator,
                    requiresRestart,
                    0,
                    "ValueProviderUnsupported"));
                continue;
            }

            if (sourceEntries.Concat(targetEntries).Any(entry => entry.IsError))
            {
                items.Add(CreateValueItem(
                    DiffType.Error,
                    representative,
                    sourceEntry,
                    targetEntry,
                    false,
                    false,
                    risk,
                    requiresAdministrator,
                    requiresRestart,
                    0,
                    "ValueProviderError"));
                continue;
            }

            if (sourceEntries.Concat(targetEntries).Any(entry => entry.IsManualActionRequired))
            {
                items.Add(CreateValueItem(
                    DiffType.ManualActionRequired,
                    representative,
                    sourceEntry,
                    targetEntry,
                    false,
                    false,
                    risk,
                    requiresAdministrator,
                    requiresRestart,
                    0,
                    "ManualValueHandlerRequired"));
                continue;
            }

            if (sourceEntry is null)
            {
                items.Add(CreateValueItem(
                    DiffType.Extra,
                    representative,
                    null,
                    targetEntry,
                    false,
                    true,
                    MaxRisk(risk, DiffRiskLevel.High),
                    requiresAdministrator,
                    requiresRestart,
                    0,
                    "ExtraValuePreserved"));
                continue;
            }

            if (targetEntry is null)
            {
                bool automatic = CanAutomaticallyRestoreValue(sourceEntry, mode);
                items.Add(CreateValueItem(
                    DiffType.Missing,
                    representative,
                    sourceEntry,
                    null,
                    automatic,
                    false,
                    risk,
                    requiresAdministrator,
                    requiresRestart,
                    0,
                    automatic ? "MissingValueCanRestore" : "MissingValueRequiresReview"));
                continue;
            }

            bool hashComparison = IsFileArea(representative.Area) &&
                (!string.IsNullOrWhiteSpace(sourceEntry.Hash) ||
                 !string.IsNullOrWhiteSpace(targetEntry.Hash));
            bool equal = hashComparison
                ? string.Equals(sourceEntry.Hash, targetEntry.Hash, StringComparison.OrdinalIgnoreCase)
                : string.Equals(sourceEntry.Value, targetEntry.Value, StringComparison.Ordinal);
            if (equal)
            {
                items.Add(CreateValueItem(
                    DiffType.ExactMatch,
                    representative,
                    sourceEntry,
                    targetEntry,
                    false,
                    false,
                    risk,
                    requiresAdministrator,
                    requiresRestart,
                    100,
                    hashComparison ? "FileHashMatch" : "ValueMatch"));
                continue;
            }

            DiffType type = hashComparison ? DiffType.FileChanged : DiffType.ValueMismatch;
            bool canRestore = CanAutomaticallyRestoreValue(sourceEntry, mode);
            items.Add(CreateValueItem(
                type,
                representative,
                sourceEntry,
                targetEntry,
                canRestore,
                false,
                risk,
                requiresAdministrator,
                requiresRestart,
                0,
                hashComparison
                    ? canRestore ? "FileChangedCanRestore" : "FileChangedRequiresReview"
                    : canRestore ? "ValueMismatchCanRestore" : "ValueMismatchRequiresReview"));
        }
    }

    private static void ComparePaths(
        IReadOnlyList<DiffPathEntry> source,
        IReadOnlyList<DiffPathEntry> target,
        DiffRestoreMode mode,
        ICollection<DiffItem> items,
        CancellationToken cancellationToken)
    {
        string[] scopes = source.Select(entry => entry.Scope)
            .Concat(target.Select(entry => entry.Scope))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(scope => scope, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (string scope in scopes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DiffPathEntry[] sourceEntries = source
                .Where(entry => entry.Scope.Equals(scope, StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => entry.Order)
                .ThenBy(entry => entry.Value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            DiffPathEntry[] targetEntries = target
                .Where(entry => entry.Scope.Equals(scope, StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => entry.Order)
                .ThenBy(entry => entry.Value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            DiffPathEntry[] combined = [.. sourceEntries, .. targetEntries];
            DiffRiskLevel risk = combined.Length == 0
                ? DiffRiskLevel.None
                : combined.Max(entry => entry.Risk);
            bool requiresAdministrator = combined.Any(entry => entry.RequiresAdministrator);
            bool requiresRestart = combined.Any(entry => entry.RequiresRestart);
            string[] sourcePaths = sourceEntries.Select(entry => NormalizePath(entry.Value)).ToArray();
            string[] targetPaths = targetEntries.Select(entry => NormalizePath(entry.Value)).ToArray();

            if (combined.Any(entry => entry.IsSensitiveExcluded))
            {
                items.Add(CreatePathSummaryItem(
                    DiffType.SensitiveExcluded,
                    scope,
                    null,
                    null,
                    false,
                    risk,
                    requiresAdministrator,
                    requiresRestart,
                    0,
                    "SensitivePathExcluded"));
                continue;
            }

            if (combined.Any(entry => !entry.IsSupported))
            {
                items.Add(CreatePathSummaryItem(
                    DiffType.Unsupported,
                    scope,
                    string.Join(';', sourceEntries.Select(entry => entry.Value)),
                    string.Join(';', targetEntries.Select(entry => entry.Value)),
                    false,
                    risk,
                    requiresAdministrator,
                    requiresRestart,
                    0,
                    "PathProviderUnsupported"));
                continue;
            }

            if (sourcePaths.SequenceEqual(targetPaths, StringComparer.OrdinalIgnoreCase))
            {
                items.Add(CreatePathSummaryItem(
                    DiffType.ExactMatch,
                    scope,
                    string.Join(';', sourceEntries.Select(entry => entry.Value)),
                    string.Join(';', targetEntries.Select(entry => entry.Value)),
                    false,
                    risk,
                    requiresAdministrator,
                    requiresRestart,
                    100,
                    "PathOrderAndValuesMatch"));
                continue;
            }

            if (HaveSameMultiset(sourcePaths, targetPaths))
            {
                bool automatic = mode is not DiffRestoreMode.Safe &&
                    sourceEntries.All(entry => entry.CanRestoreAutomatically) &&
                    IsRiskAutomaticallyAllowed(risk);
                items.Add(CreatePathSummaryItem(
                    DiffType.ValueMismatch,
                    scope,
                    string.Join(';', sourceEntries.Select(entry => entry.Value)),
                    string.Join(';', targetEntries.Select(entry => entry.Value)),
                    automatic,
                    risk,
                    requiresAdministrator,
                    requiresRestart,
                    0,
                    "PathOrderMismatch"));
                continue;
            }

            ComparePathMembership(
                sourceEntries,
                targetEntries,
                scope,
                mode,
                items,
                risk,
                requiresAdministrator,
                requiresRestart);
        }
    }

    private static void ComparePathMembership(
        IReadOnlyList<DiffPathEntry> source,
        IReadOnlyList<DiffPathEntry> target,
        string scope,
        DiffRestoreMode mode,
        ICollection<DiffItem> items,
        DiffRiskLevel risk,
        bool requiresAdministrator,
        bool requiresRestart)
    {
        Dictionary<string, Queue<DiffPathEntry>> sourceGroups = GroupPaths(source);
        Dictionary<string, Queue<DiffPathEntry>> targetGroups = GroupPaths(target);
        string[] paths = sourceGroups.Keys
            .Concat(targetGroups.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (string path in paths)
        {
            sourceGroups.TryGetValue(path, out Queue<DiffPathEntry>? sourceEntries);
            targetGroups.TryGetValue(path, out Queue<DiffPathEntry>? targetEntries);
            sourceEntries ??= new Queue<DiffPathEntry>();
            targetEntries ??= new Queue<DiffPathEntry>();
            int matchedCount = Math.Min(sourceEntries.Count, targetEntries.Count);
            for (int index = 0; index < matchedCount; index++)
            {
                DiffPathEntry sourceEntry = sourceEntries.Dequeue();
                DiffPathEntry targetEntry = targetEntries.Dequeue();
                items.Add(CreatePathItem(
                    DiffType.ExactMatch,
                    scope,
                    path,
                    index,
                    sourceEntry.Value,
                    targetEntry.Value,
                    false,
                    false,
                    risk,
                    requiresAdministrator,
                    requiresRestart,
                    100,
                    "PathEntryMatch"));
            }

            int occurrence = matchedCount;
            while (sourceEntries.Count > 0)
            {
                DiffPathEntry entry = sourceEntries.Dequeue();
                bool automatic = mode is not DiffRestoreMode.Safe &&
                    entry.CanRestoreAutomatically &&
                    IsRiskAutomaticallyAllowed(entry.Risk);
                items.Add(CreatePathItem(
                    DiffType.Missing,
                    scope,
                    path,
                    occurrence++,
                    entry.Value,
                    null,
                    automatic,
                    false,
                    entry.Risk,
                    entry.RequiresAdministrator,
                    entry.RequiresRestart,
                    0,
                    automatic ? "MissingPathCanRestore" : "MissingPathRequiresReview"));
            }

            while (targetEntries.Count > 0)
            {
                DiffPathEntry entry = targetEntries.Dequeue();
                items.Add(CreatePathItem(
                    DiffType.Extra,
                    scope,
                    path,
                    occurrence++,
                    null,
                    entry.Value,
                    false,
                    true,
                    MaxRisk(entry.Risk, DiffRiskLevel.High),
                    entry.RequiresAdministrator,
                    entry.RequiresRestart,
                    0,
                    "ExtraPathPreserved"));
            }
        }
    }

    private static EnvironmentSimilarityScore CalculateScore(IReadOnlyList<DiffItem> items)
    {
        List<DiffCategoryScore> categories = [];
        foreach ((DiffScoreCategory category, int weight) in
                 CategoryWeights.OrderBy(pair => pair.Key))
        {
            DiffItem[] categoryItems = items
                .Where(item => GetScoreCategory(item.Area) == category)
                .ToArray();
            DiffItem[] comparable = categoryItems
                .Where(item => item.Type is not (
                    DiffType.Unsupported or
                    DiffType.SensitiveExcluded or
                    DiffType.Error))
                .ToArray();
            int? score = comparable.Length == 0
                ? null
                : (int)Math.Round(
                    comparable.Average(item => item.SimilarityPercent),
                    MidpointRounding.AwayFromZero);
            categories.Add(new DiffCategoryScore(
                category,
                weight,
                score,
                comparable.Length,
                categoryItems.Length - comparable.Length));
        }

        DiffCategoryScore[] available = categories.Where(category => category.Score.HasValue).ToArray();
        int availableWeight = available.Sum(category => category.Weight);
        int? overall = availableWeight == 0
            ? null
            : (int)Math.Round(
                available.Sum(category => category.Score!.Value * category.Weight) /
                (double)availableWeight,
                MidpointRounding.AwayFromZero);
        return new EnvironmentSimilarityScore(
            overall,
            availableWeight,
            items.Count(item => item.Type == DiffType.Unsupported),
            items.Count(item => item.Type == DiffType.SensitiveExcluded),
            items.Count(item => item.Type == DiffType.Error),
            categories);
    }

    private static Dictionary<string, List<DiffValueEntry>> GroupValues(
        IEnumerable<DiffValueEntry> entries)
    {
        return entries
            .GroupBy(
                entry => $"{(int)entry.Area:D2}:{entry.Key.Trim().ToUpperInvariant()}",
                StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(entry => entry.Value, StringComparer.Ordinal)
                    .ThenBy(entry => entry.Hash, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                StringComparer.Ordinal);
    }

    private static Dictionary<string, Queue<DiffPathEntry>> GroupPaths(
        IEnumerable<DiffPathEntry> entries)
    {
        return entries
            .GroupBy(entry => NormalizePath(entry.Value), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => new Queue<DiffPathEntry>(
                    group.OrderBy(entry => entry.Order)
                        .ThenBy(entry => entry.Value, StringComparer.OrdinalIgnoreCase)),
                StringComparer.OrdinalIgnoreCase);
    }

    private static bool HaveSameMultiset(
        IEnumerable<string> source,
        IEnumerable<string> target)
    {
        string[] sourceOrdered = source.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        string[] targetOrdered = target.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        return sourceOrdered.SequenceEqual(targetOrdered, StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string value)
    {
        return value.Trim()
            .Trim('"')
            .Replace('/', '\\')
            .TrimEnd('\\')
            .ToUpperInvariant();
    }

    private static bool CanAutomaticallyRestoreValue(
        DiffValueEntry entry,
        DiffRestoreMode mode)
    {
        return mode is not DiffRestoreMode.Safe &&
            entry.CanRestoreAutomatically &&
            IsRiskAutomaticallyAllowed(entry.Risk);
    }

    private static bool IsRiskAutomaticallyAllowed(DiffRiskLevel risk)
    {
        return risk is DiffRiskLevel.None or DiffRiskLevel.Low or DiffRiskLevel.Medium;
    }

    private static bool IsFileArea(DiffArea area)
    {
        return area is DiffArea.ConfigurationFiles or DiffArea.SelectedUserFiles;
    }

    private static DiffScoreCategory GetScoreCategory(DiffArea area)
    {
        return area switch
        {
            DiffArea.Applications or DiffArea.StoreApplications =>
                DiffScoreCategory.Applications,
            DiffArea.DevelopmentEnvironment => DiffScoreCategory.DevelopmentEnvironment,
            DiffArea.PluginSettings or
            DiffArea.ConfigurationFiles or
            DiffArea.SelectedUserFiles => DiffScoreCategory.ApplicationSettings,
            DiffArea.EnvironmentVariables or DiffArea.Path =>
                DiffScoreCategory.EnvironmentAndPath,
            _ => DiffScoreCategory.FontsAndOther,
        };
    }

    private static ApplicationPolicy GetApplicationPolicy(
        IReadOnlyList<DiffApplicationEntry> entries,
        ConsolidatedApplication application)
    {
        DiffApplicationEntry[] matchingEntries = entries
            .Where(entry => application.Records.Contains(entry.Application))
            .ToArray();
        if (matchingEntries.Length == 0)
        {
            return ApplicationPolicy.Unsupported;
        }

        return new ApplicationPolicy(
            matchingEntries.All(entry => entry.IsSupported),
            matchingEntries.All(entry => entry.CanRestoreAutomatically),
            matchingEntries.Max(entry => entry.Risk),
            matchingEntries.Any(entry => entry.RequiresAdministrator),
            matchingEntries.Any(entry => entry.RequiresRestart));
    }

    private static DiffItem CreateApplicationItem(
        DiffType type,
        DiffArea area,
        string key,
        ApplicationMatch match,
        ApplicationPolicy policy,
        bool canAutomaticallyRestore,
        int similarityPercent,
        string reasonCode)
    {
        return new DiffItem(
            type,
            area,
            key,
            match.Source.Application.DisplayName,
            match.Source.Application.Version,
            match.Target.Application.Version,
            match.Confidence,
            canAutomaticallyRestore,
            false,
            false,
            policy.Risk,
            policy.RequiresAdministrator,
            policy.RequiresRestart,
            similarityPercent,
            reasonCode);
    }

    private static DiffItem CreateUnmatchedApplicationItem(
        DiffType type,
        DiffArea area,
        ConsolidatedApplication application,
        ApplicationPolicy policy,
        ApplicationMatchConfidence confidence,
        bool canAutomaticallyRestore,
        bool preserveTarget,
        int similarityPercent,
        string reasonCode)
    {
        bool isExtra = type == DiffType.Extra;
        return new DiffItem(
            type,
            area,
            GetApplicationKey(application.Application),
            application.Application.DisplayName,
            isExtra ? null : application.Application.Version,
            isExtra ? application.Application.Version : null,
            confidence,
            canAutomaticallyRestore,
            preserveTarget,
            false,
            policy.Risk,
            policy.RequiresAdministrator,
            policy.RequiresRestart,
            similarityPercent,
            reasonCode);
    }

    private static DiffItem CreateValueItem(
        DiffType type,
        DiffValueEntry representative,
        DiffValueEntry? source,
        DiffValueEntry? target,
        bool canAutomaticallyRestore,
        bool preserveTarget,
        DiffRiskLevel risk,
        bool requiresAdministrator,
        bool requiresRestart,
        int similarityPercent,
        string reasonCode)
    {
        bool redact = type == DiffType.SensitiveExcluded;
        return new DiffItem(
            type,
            representative.Area,
            representative.Key,
            representative.DisplayName,
            redact ? null : GetComparableValue(source),
            redact ? null : GetComparableValue(target),
            null,
            canAutomaticallyRestore,
            preserveTarget,
            false,
            risk,
            requiresAdministrator,
            requiresRestart,
            similarityPercent,
            reasonCode);
    }

    private static string? GetComparableValue(DiffValueEntry? entry)
    {
        return entry?.Hash ?? entry?.Value;
    }

    private static DiffItem CreatePathSummaryItem(
        DiffType type,
        string scope,
        string? source,
        string? target,
        bool canAutomaticallyRestore,
        DiffRiskLevel risk,
        bool requiresAdministrator,
        bool requiresRestart,
        int similarityPercent,
        string reasonCode)
    {
        return new DiffItem(
            type,
            DiffArea.Path,
            $"PATH:{scope}:ORDER",
            $"PATH ({scope})",
            source,
            target,
            null,
            canAutomaticallyRestore,
            false,
            false,
            risk,
            requiresAdministrator,
            requiresRestart,
            similarityPercent,
            reasonCode);
    }

    private static DiffItem CreatePathItem(
        DiffType type,
        string scope,
        string normalizedPath,
        int occurrence,
        string? source,
        string? target,
        bool canAutomaticallyRestore,
        bool preserveTarget,
        DiffRiskLevel risk,
        bool requiresAdministrator,
        bool requiresRestart,
        int similarityPercent,
        string reasonCode)
    {
        return new DiffItem(
            type,
            DiffArea.Path,
            $"PATH:{scope}:{normalizedPath}:{occurrence:D4}",
            source ?? target ?? normalizedPath,
            source,
            target,
            null,
            canAutomaticallyRestore,
            preserveTarget,
            false,
            risk,
            requiresAdministrator,
            requiresRestart,
            similarityPercent,
            reasonCode);
    }

    private static string GetApplicationKey(ApplicationDescriptor application)
    {
        return FirstNonEmpty(
            application.WingetPackageId,
            application.MsixPackageFamilyName,
            application.MsiProductCode,
            $"{application.Publisher}|{application.DisplayName}")
            .Trim()
            .ToUpperInvariant();
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.First(value => !string.IsNullOrWhiteSpace(value))!;
    }

    private static DiffRiskLevel MaxRisk(DiffRiskLevel first, DiffRiskLevel second)
    {
        return first >= second ? first : second;
    }

    private static void ValidateState(DiffEnvironmentState state, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(state, parameterName);
        if (state.Applications is null ||
            state.StoreApplications is null ||
            state.Values is null ||
            state.PathEntries is null ||
            state.Applications.Any(entry => entry is null || entry.Application is null) ||
            state.StoreApplications.Any(entry => entry is null || entry.Application is null) ||
            state.Values.Any(entry =>
                entry is null ||
                string.IsNullOrWhiteSpace(entry.Key) ||
                string.IsNullOrWhiteSpace(entry.DisplayName) ||
                entry.Area is DiffArea.Applications or DiffArea.StoreApplications or DiffArea.Path) ||
            state.PathEntries.Any(entry =>
                entry is null ||
                string.IsNullOrWhiteSpace(entry.Value) ||
                string.IsNullOrWhiteSpace(entry.Scope)))
        {
            throw new ArgumentException("The diff environment state is invalid.", parameterName);
        }
    }

    private sealed record ApplicationPolicy(
        bool IsSupported,
        bool CanRestoreAutomatically,
        DiffRiskLevel Risk,
        bool RequiresAdministrator,
        bool RequiresRestart)
    {
        public static ApplicationPolicy Unsupported { get; } = new(
            false,
            false,
            DiffRiskLevel.High,
            false,
            false);

        public static ApplicationPolicy Combine(
            ApplicationPolicy source,
            ApplicationPolicy target)
        {
            return new ApplicationPolicy(
                source.IsSupported && target.IsSupported,
                source.CanRestoreAutomatically && target.CanRestoreAutomatically,
                MaxRisk(source.Risk, target.Risk),
                source.RequiresAdministrator || target.RequiresAdministrator,
                source.RequiresRestart || target.RequiresRestart);
        }
    }
}
