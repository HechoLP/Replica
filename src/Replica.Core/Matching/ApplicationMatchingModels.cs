using Replica.Core.Scanning;
using Replica.Core.Snapshots;

namespace Replica.Core.Matching;

public enum ApplicationMatchConfidence
{
    Exact,
    High,
    Medium,
    Low,
    Unknown,
}

public enum ApplicationMatchBasis
{
    WingetPackageIdentifier,
    MsixPackageFamilyName,
    MsiProductCode,
    PublisherAndDisplayName,
    NormalizedNameAndInstallLocation,
    Heuristic,
    None,
}

public enum ApplicationReleaseChannel
{
    Stable,
    Preview,
    Beta,
    ReleaseCandidate,
}

public enum ApplicationComponentKind
{
    Application,
    Runtime,
    Sdk,
}

public enum ApplicationVersionKind
{
    Semantic,
    System,
    Date,
    Unknown,
}

public enum ApplicationVersionComparison
{
    Equal,
    SourceNewer,
    TargetNewer,
    Incomparable,
    Unknown,
}

public sealed record ApplicationDescriptor(
    string DisplayName,
    string? Version,
    string? Publisher,
    string? InstallLocation,
    string? Architecture,
    string? WingetPackageId,
    string? MsixPackageFamilyName,
    string? MsiProductCode)
{
    public static ApplicationDescriptor FromScannedApplication(ScannedApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        return new ApplicationDescriptor(
            application.Name,
            application.Version,
            application.Publisher,
            application.InstallLocation,
            application.Architecture,
            application.WinGetId,
            application.PackageFamilyName,
            application.MsiProductCode);
    }

    public static ApplicationDescriptor FromSnapshotApplication(ReplicaApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        return new ApplicationDescriptor(
            application.DisplayName,
            application.Version,
            application.Publisher,
            null,
            application.Architecture,
            application.PackageIdentity.WingetPackageId,
            application.PackageIdentity.MsixPackageFamilyName,
            application.PackageIdentity.MsiProductCode);
    }
}

public sealed record NormalizedApplicationIdentity(
    string Name,
    string Publisher,
    string InstallLocation,
    string Architecture,
    ApplicationReleaseChannel ReleaseChannel,
    ApplicationComponentKind Component,
    IReadOnlyList<string> EditionTokens);

public sealed record ConsolidatedApplication(
    ApplicationDescriptor Application,
    IReadOnlyList<ApplicationDescriptor> Records)
{
    public int RecordCount => Records.Count;
}

public sealed record ApplicationMatch(
    ConsolidatedApplication Source,
    ConsolidatedApplication Target,
    ApplicationMatchConfidence Confidence,
    ApplicationMatchBasis Basis,
    int SimilarityScore,
    string ReasonCode)
{
    public bool AllowsAutomaticAction => Confidence is
        ApplicationMatchConfidence.Exact or
        ApplicationMatchConfidence.High or
        ApplicationMatchConfidence.Medium;
}

public sealed record ApplicationMatchingResult(
    IReadOnlyList<ApplicationMatch> Matches,
    IReadOnlyList<ConsolidatedApplication> UnmatchedSource,
    IReadOnlyList<ConsolidatedApplication> UnmatchedTarget,
    IReadOnlyList<ApplicationMatchAmbiguity> Ambiguous);

public sealed record ApplicationMatchAmbiguity(
    ConsolidatedApplication Source,
    IReadOnlyList<ConsolidatedApplication> Candidates,
    ApplicationMatchConfidence Confidence,
    string ReasonCode);

public sealed record ApplicationVersionComparisonResult(
    ApplicationVersionComparison Comparison,
    ApplicationVersionKind SourceKind,
    ApplicationVersionKind TargetKind,
    string? SourceNormalized,
    string? TargetNormalized,
    string ReasonCode)
{
    public bool AllowsAutomaticVersionChange => Comparison ==
        ApplicationVersionComparison.SourceNewer;
}

public sealed record ApplicationAutomationDecision(
    bool CanAutomaticallyInstall,
    bool CanAutomaticallyUpdate,
    bool CanAutomaticallyDowngrade,
    string ReasonCode);
