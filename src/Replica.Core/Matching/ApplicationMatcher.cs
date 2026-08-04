using Replica.Core.Services;

namespace Replica.Core.Matching;

public sealed class ApplicationMatcher : IApplicationMatcher
{
    private readonly IApplicationIdentityNormalizer _normalizer;

    public ApplicationMatcher(IApplicationIdentityNormalizer normalizer)
    {
        _normalizer = normalizer;
    }

    public ApplicationMatch Evaluate(
        ApplicationDescriptor source,
        ApplicationDescriptor target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        return EvaluateConsolidated(
            new ConsolidatedApplication(source, [source]),
            new ConsolidatedApplication(target, [target]));
    }

    public ApplicationMatchingResult Match(
        IReadOnlyList<ApplicationDescriptor> source,
        IReadOnlyList<ApplicationDescriptor> target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (source.Any(application => application is null) ||
            target.Any(application => application is null))
        {
            throw new ArgumentException("Application collections cannot contain null entries.");
        }

        List<ConsolidatedApplication> sourceApplications = Consolidate(source);
        List<ConsolidatedApplication> targetApplications = Consolidate(target);
        HashSet<int> usedTargets = [];
        List<ApplicationMatch> matches = [];
        List<ConsolidatedApplication> unmatchedSource = [];
        List<ApplicationMatchAmbiguity> ambiguities = [];

        foreach (ConsolidatedApplication sourceApplication in OrderSourcesByBestMatch(
                     sourceApplications,
                     targetApplications))
        {
            List<(int Index, ApplicationMatch Match)> candidates = targetApplications
                .Select((targetApplication, index) =>
                    (index, EvaluateConsolidated(sourceApplication, targetApplication)))
                .Where(candidate =>
                    !usedTargets.Contains(candidate.index) &&
                    candidate.Item2.Confidence != ApplicationMatchConfidence.Unknown)
                .OrderByDescending(candidate => ConfidenceRank(candidate.Item2.Confidence))
                .ThenBy(candidate => BasisRank(candidate.Item2.Basis))
                .ThenByDescending(candidate => candidate.Item2.SimilarityScore)
                .ToList();

            if (candidates.Count == 0)
            {
                unmatchedSource.Add(sourceApplication);
                continue;
            }

            (int Index, ApplicationMatch Match) best = candidates[0];
            List<(int Index, ApplicationMatch Match)> tied = candidates
                .Where(candidate =>
                    ConfidenceRank(candidate.Match.Confidence) == ConfidenceRank(best.Match.Confidence) &&
                    BasisRank(candidate.Match.Basis) == BasisRank(best.Match.Basis) &&
                    candidate.Match.SimilarityScore == best.Match.SimilarityScore)
                .ToList();
            if (tied.Count > 1)
            {
                ambiguities.Add(new ApplicationMatchAmbiguity(
                    sourceApplication,
                    tied.Select(candidate => candidate.Match.Target).ToArray(),
                    best.Match.Confidence,
                    "AmbiguousCandidates"));
                continue;
            }

            usedTargets.Add(best.Index);
            matches.Add(best.Match);
        }

        ConsolidatedApplication[] unmatchedTarget = targetApplications
            .Where((_, index) => !usedTargets.Contains(index))
            .ToArray();
        return new ApplicationMatchingResult(
            matches,
            unmatchedSource,
            unmatchedTarget,
            ambiguities);
    }

    private IReadOnlyList<ConsolidatedApplication> OrderSourcesByBestMatch(
        IEnumerable<ConsolidatedApplication> source,
        IReadOnlyList<ConsolidatedApplication> target)
    {
        return source
            .Select(application =>
            {
                ApplicationMatch? best = target
                    .Select(candidate => EvaluateConsolidated(application, candidate))
                    .OrderByDescending(match => ConfidenceRank(match.Confidence))
                    .ThenBy(match => BasisRank(match.Basis))
                    .ThenByDescending(match => match.SimilarityScore)
                    .FirstOrDefault();
                return new
                {
                    Application = application,
                    Confidence = best is null ? 0 : ConfidenceRank(best.Confidence),
                    Basis = best is null ? int.MaxValue : BasisRank(best.Basis),
                    Score = best?.SimilarityScore ?? 0,
                };
            })
            .OrderByDescending(item => item.Confidence)
            .ThenBy(item => item.Basis)
            .ThenByDescending(item => item.Score)
            .ThenBy(
                item => item.Application.Application.DisplayName,
                StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Application)
            .ToArray();
    }

    public ApplicationMatchConfidence AssessIdentityConfidence(
        ApplicationDescriptor application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (HasValue(application.WingetPackageId) ||
            HasValue(application.MsixPackageFamilyName) ||
            NormalizeMsiProductCode(application.MsiProductCode).Length > 0)
        {
            return ApplicationMatchConfidence.Exact;
        }

        NormalizedApplicationIdentity identity = _normalizer.Normalize(application);
        if (identity.Name.Length > 0 && identity.Publisher.Length > 0)
        {
            return ApplicationMatchConfidence.High;
        }

        if (identity.Name.Length > 0 && identity.InstallLocation.Length > 0)
        {
            return ApplicationMatchConfidence.Medium;
        }

        return identity.Name.Length > 0
            ? ApplicationMatchConfidence.Low
            : ApplicationMatchConfidence.Unknown;
    }

    private ApplicationMatch EvaluateConsolidated(
        ConsolidatedApplication source,
        ConsolidatedApplication target)
    {
        ApplicationDescriptor sourceApplication = source.Application;
        ApplicationDescriptor targetApplication = target.Application;

        if (StableIdEquals(
                sourceApplication.WingetPackageId,
                targetApplication.WingetPackageId))
        {
            return CreateMatch(
                source,
                target,
                ApplicationMatchConfidence.Exact,
                ApplicationMatchBasis.WingetPackageIdentifier,
                100,
                "WingetPackageIdentifier");
        }

        if (StableIdConflicts(
                sourceApplication.WingetPackageId,
                targetApplication.WingetPackageId))
        {
            return CreateMatch(
                source,
                target,
                ApplicationMatchConfidence.Unknown,
                ApplicationMatchBasis.None,
                0,
                "WingetPackageIdentifierConflict");
        }

        if (StableIdEquals(
                sourceApplication.MsixPackageFamilyName,
                targetApplication.MsixPackageFamilyName))
        {
            return CreateMatch(
                source,
                target,
                ApplicationMatchConfidence.Exact,
                ApplicationMatchBasis.MsixPackageFamilyName,
                100,
                "MsixPackageFamilyName");
        }

        if (StableIdConflicts(
                sourceApplication.MsixPackageFamilyName,
                targetApplication.MsixPackageFamilyName))
        {
            return CreateMatch(
                source,
                target,
                ApplicationMatchConfidence.Unknown,
                ApplicationMatchBasis.None,
                0,
                "MsixPackageFamilyNameConflict");
        }

        string sourceMsi = NormalizeMsiProductCode(sourceApplication.MsiProductCode);
        string targetMsi = NormalizeMsiProductCode(targetApplication.MsiProductCode);
        if (sourceMsi.Length > 0 && sourceMsi == targetMsi)
        {
            return CreateMatch(
                source,
                target,
                ApplicationMatchConfidence.Exact,
                ApplicationMatchBasis.MsiProductCode,
                100,
                "MsiProductCode");
        }

        if (sourceMsi.Length > 0 && targetMsi.Length > 0)
        {
            return CreateMatch(
                source,
                target,
                ApplicationMatchConfidence.Unknown,
                ApplicationMatchBasis.None,
                0,
                "MsiProductCodeConflict");
        }

        NormalizedApplicationIdentity sourceIdentity = _normalizer.Normalize(sourceApplication);
        NormalizedApplicationIdentity targetIdentity = _normalizer.Normalize(targetApplication);
        if (!QualifiersMatch(sourceIdentity, targetIdentity))
        {
            return CreateMatch(
                source,
                target,
                ApplicationMatchConfidence.Unknown,
                ApplicationMatchBasis.None,
                0,
                "QualifierMismatch");
        }

        if (sourceIdentity.Name.Length > 0 &&
            sourceIdentity.Name == targetIdentity.Name &&
            sourceIdentity.Publisher.Length > 0 &&
            sourceIdentity.Publisher == targetIdentity.Publisher)
        {
            return CreateMatch(
                source,
                target,
                ApplicationMatchConfidence.High,
                ApplicationMatchBasis.PublisherAndDisplayName,
                90,
                "PublisherAndDisplayName");
        }

        if (sourceIdentity.Name.Length > 0 &&
            sourceIdentity.Name == targetIdentity.Name &&
            sourceIdentity.InstallLocation.Length > 0 &&
            sourceIdentity.InstallLocation == targetIdentity.InstallLocation)
        {
            return CreateMatch(
                source,
                target,
                ApplicationMatchConfidence.Medium,
                ApplicationMatchBasis.NormalizedNameAndInstallLocation,
                75,
                "NormalizedNameAndInstallLocation");
        }

        int similarity = CalculateNameSimilarity(sourceIdentity.Name, targetIdentity.Name);
        if (similarity >= 55)
        {
            return CreateMatch(
                source,
                target,
                ApplicationMatchConfidence.Low,
                ApplicationMatchBasis.Heuristic,
                similarity,
                "HeuristicNameSimilarity");
        }

        return CreateMatch(
            source,
            target,
            ApplicationMatchConfidence.Unknown,
            ApplicationMatchBasis.None,
            similarity,
            "NoReliableIdentity");
    }

    private List<ConsolidatedApplication> Consolidate(
        IReadOnlyList<ApplicationDescriptor> applications)
    {
        List<List<ApplicationDescriptor>> groups = [];
        foreach (ApplicationDescriptor application in applications)
        {
            List<ApplicationDescriptor>? selectedGroup = null;
            int selectedRank = 0;
            foreach (List<ApplicationDescriptor> group in groups)
            {
                ApplicationMatch match = Evaluate(application, MergeDescriptors(group));
                int rank = ConfidenceRank(match.Confidence);
                if (rank >= ConfidenceRank(ApplicationMatchConfidence.Medium) &&
                    rank > selectedRank)
                {
                    selectedGroup = group;
                    selectedRank = rank;
                }
            }

            if (selectedGroup is null)
            {
                groups.Add([application]);
            }
            else
            {
                selectedGroup.Add(application);
            }
        }

        return groups
            .Select(group => new ConsolidatedApplication(
                MergeDescriptors(group),
                group.ToArray()))
            .OrderBy(group => group.Application.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static ApplicationDescriptor MergeDescriptors(
        IReadOnlyList<ApplicationDescriptor> records)
    {
        ApplicationDescriptor primary = records
            .OrderByDescending(CompletenessScore)
            .ThenBy(application => application.DisplayName, StringComparer.OrdinalIgnoreCase)
            .First();
        return primary with
        {
            Version = FirstValue(primary.Version, records.Select(record => record.Version)),
            Publisher = FirstValue(primary.Publisher, records.Select(record => record.Publisher)),
            InstallLocation = FirstValue(
                primary.InstallLocation,
                records.Select(record => record.InstallLocation)),
            Architecture = FirstValue(
                primary.Architecture,
                records.Select(record => record.Architecture)),
            WingetPackageId = FirstValue(
                primary.WingetPackageId,
                records.Select(record => record.WingetPackageId)),
            MsixPackageFamilyName = FirstValue(
                primary.MsixPackageFamilyName,
                records.Select(record => record.MsixPackageFamilyName)),
            MsiProductCode = FirstValue(
                primary.MsiProductCode,
                records.Select(record => record.MsiProductCode)),
        };
    }

    private static int CompletenessScore(ApplicationDescriptor application)
    {
        return new[]
        {
            application.Version,
            application.Publisher,
            application.InstallLocation,
            application.Architecture,
            application.WingetPackageId,
            application.MsixPackageFamilyName,
            application.MsiProductCode,
        }.Count(HasValue);
    }

    private static string? FirstValue(string? preferred, IEnumerable<string?> values)
    {
        return HasValue(preferred)
            ? preferred
            : values.FirstOrDefault(HasValue);
    }

    private static bool QualifiersMatch(
        NormalizedApplicationIdentity source,
        NormalizedApplicationIdentity target)
    {
        return source.ReleaseChannel == target.ReleaseChannel &&
            source.Component == target.Component &&
            source.EditionTokens.SequenceEqual(target.EditionTokens, StringComparer.Ordinal);
    }

    private static int CalculateNameSimilarity(string source, string target)
    {
        if (source.Length == 0 || target.Length == 0)
        {
            return 0;
        }

        if (source == target)
        {
            return 70;
        }

        HashSet<string> sourceTokens = source.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        HashSet<string> targetTokens = target.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        int intersection = sourceTokens.Count(targetTokens.Contains);
        int union = sourceTokens.Count + targetTokens.Count - intersection;
        return union == 0 ? 0 : (int)Math.Round(intersection * 100d / union);
    }

    private static string NormalizeMsiProductCode(string? value)
    {
        return Guid.TryParse(value?.Trim().Trim('{', '}'), out Guid productCode)
            ? productCode.ToString("D").ToUpperInvariant()
            : string.Empty;
    }

    private static bool StableIdEquals(string? source, string? target)
    {
        return !string.IsNullOrWhiteSpace(source) &&
            !string.IsNullOrWhiteSpace(target) &&
            source.Trim().Equals(target.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool StableIdConflicts(string? source, string? target)
    {
        return !string.IsNullOrWhiteSpace(source) &&
            !string.IsNullOrWhiteSpace(target) &&
            !source.Trim().Equals(target.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasValue(string? value)
    {
        return !string.IsNullOrWhiteSpace(value);
    }

    private static int ConfidenceRank(ApplicationMatchConfidence confidence)
    {
        return confidence switch
        {
            ApplicationMatchConfidence.Exact => 5,
            ApplicationMatchConfidence.High => 4,
            ApplicationMatchConfidence.Medium => 3,
            ApplicationMatchConfidence.Low => 2,
            ApplicationMatchConfidence.Unknown => 1,
            _ => 0,
        };
    }

    private static int BasisRank(ApplicationMatchBasis basis)
    {
        return basis switch
        {
            ApplicationMatchBasis.WingetPackageIdentifier => 1,
            ApplicationMatchBasis.MsixPackageFamilyName => 2,
            ApplicationMatchBasis.MsiProductCode => 3,
            ApplicationMatchBasis.PublisherAndDisplayName => 4,
            ApplicationMatchBasis.NormalizedNameAndInstallLocation => 5,
            ApplicationMatchBasis.Heuristic => 6,
            ApplicationMatchBasis.None => 7,
            _ => 8,
        };
    }

    private static ApplicationMatch CreateMatch(
        ConsolidatedApplication source,
        ConsolidatedApplication target,
        ApplicationMatchConfidence confidence,
        ApplicationMatchBasis basis,
        int similarityScore,
        string reasonCode)
    {
        return new ApplicationMatch(
            source,
            target,
            confidence,
            basis,
            similarityScore,
            reasonCode);
    }
}
