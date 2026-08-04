using Replica.Core.Matching;

namespace Replica.Core.Tests;

public sealed class ApplicationMatcherTests
{
    private readonly ApplicationIdentityNormalizer _normalizer = new();
    private readonly ApplicationMatcher _matcher;

    public ApplicationMatcherTests()
    {
        _matcher = new ApplicationMatcher(_normalizer);
    }

    [Fact]
    public void Evaluate_UsesWingetIdentifierBeforeAllOtherEvidence()
    {
        ApplicationDescriptor source = App(
            "Source name",
            wingetId: "Contoso.Editor",
            msixId: "Source_123",
            msiId: "57BA5FE3-6374-402E-B593-C32376223E34");
        ApplicationDescriptor target = App(
            "Different target name",
            wingetId: "contoso.editor",
            msixId: "Target_456",
            msiId: "10F111E2-53DA-4A1B-9E89-E483795FE177");

        ApplicationMatch match = _matcher.Evaluate(source, target);

        Assert.Equal(ApplicationMatchConfidence.Exact, match.Confidence);
        Assert.Equal(ApplicationMatchBasis.WingetPackageIdentifier, match.Basis);
        Assert.True(match.AllowsAutomaticAction);
    }

    [Fact]
    public void Evaluate_UsesMsixThenMsiStableIdentities()
    {
        ApplicationMatch msix = _matcher.Evaluate(
            App("One", msixId: "Contoso.App_123"),
            App("Two", msixId: "contoso.app_123"));
        ApplicationMatch msi = _matcher.Evaluate(
            App("One", msiId: "{57BA5FE3-6374-402E-B593-C32376223E34}"),
            App("Two", msiId: "57ba5fe3-6374-402e-b593-c32376223e34"));

        Assert.Equal(ApplicationMatchBasis.MsixPackageFamilyName, msix.Basis);
        Assert.Equal(ApplicationMatchConfidence.Exact, msix.Confidence);
        Assert.Equal(ApplicationMatchBasis.MsiProductCode, msi.Basis);
        Assert.Equal(ApplicationMatchConfidence.Exact, msi.Confidence);
    }

    [Fact]
    public void Evaluate_DoesNotFallBackWhenStableIdentifiersConflict()
    {
        ApplicationMatch match = _matcher.Evaluate(
            App("Contoso Editor", publisher: "Contoso", wingetId: "Contoso.Editor"),
            App("Contoso Editor", publisher: "Contoso", wingetId: "Other.Editor"));

        Assert.Equal(ApplicationMatchConfidence.Unknown, match.Confidence);
        Assert.Equal("WingetPackageIdentifierConflict", match.ReasonCode);
        Assert.False(match.AllowsAutomaticAction);
    }

    [Fact]
    public void Evaluate_NormalizesDisplayAndPublisherVariations()
    {
        ApplicationDescriptor source = App(
            "Contoso Éditor™ v2.4 x64 (User)",
            publisher: "Contoso, Inc.");
        ApplicationDescriptor target = App(
            "CONTOSO EDITOR 64-bit",
            publisher: "CONTOSO Corporation");
        NormalizedApplicationIdentity sourceIdentity = _normalizer.Normalize(source);
        NormalizedApplicationIdentity targetIdentity = _normalizer.Normalize(target);
        ApplicationMatch match = _matcher.Evaluate(source, target);

        Assert.Equal(sourceIdentity.Name, targetIdentity.Name);
        Assert.Equal(sourceIdentity.Publisher, targetIdentity.Publisher);
        Assert.Equal(ApplicationMatchConfidence.High, match.Confidence);
        Assert.Equal(ApplicationMatchBasis.PublisherAndDisplayName, match.Basis);
    }

    [Fact]
    public void Evaluate_MatchesX86AndX64VariantsOfSameApplication()
    {
        ApplicationMatch match = _matcher.Evaluate(
            App("Fabrikam Tool x86", publisher: "Fabrikam", architecture: "x86"),
            App("Fabrikam Tool 64-bit", publisher: "Fabrikam Ltd", architecture: "AMD64"));

        Assert.Equal(ApplicationMatchConfidence.High, match.Confidence);
    }

    [Fact]
    public void Evaluate_UsesNormalizedNameAndInstallLocationAtMediumConfidence()
    {
        ApplicationMatch match = _matcher.Evaluate(
            App("Contoso Tool v2", location: "C:/Apps/Contoso/"),
            App("CONTOSO TOOL", location: "c:\\apps\\contoso"));

        Assert.Equal(ApplicationMatchConfidence.Medium, match.Confidence);
        Assert.Equal(ApplicationMatchBasis.NormalizedNameAndInstallLocation, match.Basis);
        Assert.True(match.AllowsAutomaticAction);
    }

    [Fact]
    public void Evaluate_DistinguishesRuntimeFromMainApplication()
    {
        ApplicationMatch match = _matcher.Evaluate(
            App("Contoso Desktop Runtime", publisher: "Contoso"),
            App("Contoso Desktop", publisher: "Contoso"));

        Assert.Equal(ApplicationMatchConfidence.Unknown, match.Confidence);
        Assert.Equal("QualifierMismatch", match.ReasonCode);
    }

    [Fact]
    public void Evaluate_DistinguishesPreviewAndStableChannels()
    {
        ApplicationMatch match = _matcher.Evaluate(
            App("Contoso Studio Preview", publisher: "Contoso"),
            App("Contoso Studio", publisher: "Contoso"));

        Assert.Equal(ApplicationMatchConfidence.Unknown, match.Confidence);
        Assert.False(match.AllowsAutomaticAction);
    }

    [Fact]
    public void Evaluate_DistinguishesEditionsAndSdkComponents()
    {
        ApplicationMatch editions = _matcher.Evaluate(
            App("Contoso IDE Community Edition 2025", publisher: "Contoso"),
            App("Contoso IDE Professional 2025", publisher: "Contoso"));
        ApplicationMatch sdk = _matcher.Evaluate(
            App("Contoso Platform SDK", publisher: "Contoso"),
            App("Contoso Platform", publisher: "Contoso"));

        Assert.Equal(ApplicationMatchConfidence.Unknown, editions.Confidence);
        Assert.Equal(ApplicationMatchConfidence.Unknown, sdk.Confidence);
    }

    [Fact]
    public void Match_ReportsAmbiguousLowConfidenceCandidates()
    {
        ApplicationMatchingResult result = _matcher.Match(
            [App("Contoso Photo Editor Classic")],
            [
                App("Contoso Photo Editor Plus"),
                App("Contoso Photo Editor Deluxe"),
            ]);

        ApplicationMatchAmbiguity ambiguity = Assert.Single(result.Ambiguous);
        Assert.Equal(ApplicationMatchConfidence.Low, ambiguity.Confidence);
        Assert.Equal(2, ambiguity.Candidates.Count);
        Assert.Empty(result.Matches);
    }

    [Fact]
    public void Match_MergesDuplicateRecordsBeforePairing()
    {
        ApplicationMatchingResult result = _matcher.Match(
            [
                App("Contoso Editor", wingetId: "Contoso.Editor", version: "1.0"),
                App(
                    "Contoso Editor",
                    publisher: "Contoso",
                    location: "C:\\Contoso",
                    wingetId: "contoso.editor"),
            ],
            [App("Editor", wingetId: "Contoso.Editor", version: "1.0")]);

        ApplicationMatch match = Assert.Single(result.Matches);
        Assert.Equal(2, match.Source.RecordCount);
        Assert.Equal("Contoso", match.Source.Application.Publisher);
        Assert.Equal("C:\\Contoso", match.Source.Application.InstallLocation);
        Assert.Empty(result.UnmatchedSource);
        Assert.Empty(result.UnmatchedTarget);
    }

    [Fact]
    public void Match_ReservesTargetForExactIdentityBeforeHeuristicCandidate()
    {
        ApplicationMatchingResult result = _matcher.Match(
            [
                App("A Contoso Editor"),
                App("Z Unrelated Name", wingetId: "Contoso.Editor"),
            ],
            [App("Contoso Editor", wingetId: "Contoso.Editor")]);

        ApplicationMatch match = Assert.Single(result.Matches);
        Assert.Equal("Z Unrelated Name", match.Source.Application.DisplayName);
        Assert.Equal(ApplicationMatchConfidence.Exact, match.Confidence);
        Assert.Contains(
            result.UnmatchedSource,
            application => application.Application.DisplayName == "A Contoso Editor");
    }

    [Fact]
    public void Normalize_PreservesLocalizedTextAndMeaningfulQualifiers()
    {
        NormalizedApplicationIdentity identity = _normalizer.Normalize(
            App("콘토소 도구 SDK Preview Community Edition 2025 (사용자)", "Contoso GmbH"));

        Assert.Contains("콘토소", identity.Name, StringComparison.Ordinal);
        Assert.Equal("contoso", identity.Publisher);
        Assert.Equal(ApplicationComponentKind.Sdk, identity.Component);
        Assert.Equal(ApplicationReleaseChannel.Preview, identity.ReleaseChannel);
        Assert.Equal(["community"], identity.EditionTokens);
    }

    private static ApplicationDescriptor App(
        string name,
        string? publisher = null,
        string? version = null,
        string? location = null,
        string? architecture = null,
        string? wingetId = null,
        string? msixId = null,
        string? msiId = null)
    {
        return new ApplicationDescriptor(
            name,
            version,
            publisher,
            location,
            architecture,
            wingetId,
            msixId,
            msiId);
    }
}
