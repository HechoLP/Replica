using Replica.Core.Updates;

namespace Replica.Core.Tests;

public sealed class SemanticVersionTests
{
    [Theory]
    [InlineData("v1.0.0", "1.0.0", UpdateChannel.Stable)]
    [InlineData("v1.1.0-beta.1", "1.1.0-beta.1", UpdateChannel.Beta)]
    [InlineData("v1.1.0-rc.1", "1.1.0-rc.1", UpdateChannel.Beta)]
    [InlineData("v0.1.0-alpha.1", "0.1.0-alpha.1", UpdateChannel.Alpha)]
    public void TryParse_ParsesSupportedReplicaTags(
        string input,
        string expected,
        UpdateChannel channel)
    {
        bool parsed = SemanticVersion.TryParse(input, out SemanticVersion? result);

        Assert.True(parsed);
        Assert.Equal(expected, result?.ToString());
        Assert.Equal(channel, result?.Channel);
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1.0.0-preview.1")]
    [InlineData("1.0.0-beta")]
    [InlineData("1.00.0")]
    [InlineData("latest")]
    public void TryParse_RejectsUnsupportedOrNonCanonicalVersions(string input)
    {
        Assert.False(SemanticVersion.TryParse(input, out _));
    }

    [Fact]
    public void CompareTo_UsesSemanticPrereleaseOrder()
    {
        SemanticVersion.TryParse("1.0.0-alpha.2", out SemanticVersion? alpha);
        SemanticVersion.TryParse("1.0.0-beta.1", out SemanticVersion? beta);
        SemanticVersion.TryParse("1.0.0-rc.1", out SemanticVersion? rc);
        SemanticVersion.TryParse("1.0.0", out SemanticVersion? stable);

        Assert.True(alpha!.CompareTo(beta) < 0);
        Assert.True(beta!.CompareTo(rc) < 0);
        Assert.True(rc!.CompareTo(stable) < 0);
    }
}
