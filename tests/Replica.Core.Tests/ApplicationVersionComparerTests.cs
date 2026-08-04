using Replica.Core.Matching;

namespace Replica.Core.Tests;

public sealed class ApplicationVersionComparerTests
{
    private readonly ApplicationVersionComparer _comparer = new();

    [Theory]
    [InlineData("v1.2.3", "1.2.3.0")]
    [InlineData("2.0", "2.0.0")]
    [InlineData("v 2", "2.0")]
    [InlineData("2025.01.02", "20250102")]
    [InlineData("opaque-build", "OPAQUE-BUILD")]
    public void Compare_EquivalentVersions_ReturnsEqual(string source, string target)
    {
        ApplicationVersionComparisonResult result = _comparer.Compare(source, target);

        Assert.Equal(ApplicationVersionComparison.Equal, result.Comparison);
        Assert.False(result.AllowsAutomaticVersionChange);
    }

    [Theory]
    [InlineData("2.0.0", "1.9.9", ApplicationVersionComparison.SourceNewer)]
    [InlineData("1.2.3.4", "1.2.3.5", ApplicationVersionComparison.TargetNewer)]
    [InlineData("2025-03-01", "2024.12.31", ApplicationVersionComparison.SourceNewer)]
    [InlineData("20240101", "2024-01-02", ApplicationVersionComparison.TargetNewer)]
    [InlineData("1.0.0-preview.2", "1.0.0-preview.1", ApplicationVersionComparison.SourceNewer)]
    [InlineData("1.0.0-rc.1", "1.0.0-rc.2", ApplicationVersionComparison.TargetNewer)]
    public void Compare_ParsedVersions_ReturnsOrdering(
        string source,
        string target,
        ApplicationVersionComparison expected)
    {
        ApplicationVersionComparisonResult result = _comparer.Compare(source, target);

        Assert.Equal(expected, result.Comparison);
    }

    [Theory]
    [InlineData("1.0.0-preview.1", "1.0.0")]
    [InlineData("1.0.0-beta.1", "1.0.0-rc.1")]
    [InlineData("2025-01-01", "1.0.0")]
    [InlineData("vendor build one", "vendor build two")]
    [InlineData("2025.02.30", "2025.02.28")]
    [InlineData("20251301", "20250101")]
    public void Compare_IncompatibleOrUnparseableVersions_ReturnsIncomparable(
        string source,
        string target)
    {
        ApplicationVersionComparisonResult result = _comparer.Compare(source, target);

        Assert.Equal(ApplicationVersionComparison.Incomparable, result.Comparison);
        Assert.False(result.AllowsAutomaticVersionChange);
    }

    [Fact]
    public void Compare_MissingVersion_ReturnsUnknown()
    {
        ApplicationVersionComparisonResult result = _comparer.Compare(null, "1.0");

        Assert.Equal(ApplicationVersionComparison.Unknown, result.Comparison);
    }
}
