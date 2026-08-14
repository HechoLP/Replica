using Replica.Core.Snapshots;

namespace Replica.Core.Tests;

public sealed class SnapshotOpenArgumentsParserTests
{
    [Fact]
    public void TryParse_AcceptsOneFullyQualifiedReplicaPath()
    {
        string path = Path.Combine(Path.GetTempPath(), "recovery.replica");

        bool parsed = SnapshotOpenArgumentsParser.TryParse(
            [SnapshotOpenArgumentsParser.OpenSwitch, path],
            out string? snapshotPath);

        Assert.True(parsed);
        Assert.Equal(Path.GetFullPath(path), snapshotPath);
    }

    [Theory]
    [InlineData("snapshot.replica")]
    [InlineData("C:\\Snapshots\\snapshot.zip")]
    [InlineData("C:\\Snapshots\\bad\nname.replica")]
    public void TryParse_RejectsUnsafeOrUnsupportedPaths(string path)
    {
        bool parsed = SnapshotOpenArgumentsParser.TryParse(
            [SnapshotOpenArgumentsParser.OpenSwitch, path],
            out string? snapshotPath);

        Assert.False(parsed);
        Assert.Null(snapshotPath);
    }

    [Fact]
    public void TryParse_RejectsAdditionalArguments()
    {
        bool parsed = SnapshotOpenArgumentsParser.TryParse(
            [SnapshotOpenArgumentsParser.OpenSwitch, "C:\\snapshot.replica", "extra"],
            out string? snapshotPath);

        Assert.False(parsed);
        Assert.Null(snapshotPath);
    }

    [Fact]
    public void TryParseAssociatedFileAcceptsOneAbsoluteReplicaPath()
    {
        string path = Path.Combine(Path.GetTempPath(), "finder.replica");

        bool parsed = SnapshotOpenArgumentsParser.TryParseAssociatedFile([path], out string? snapshotPath);

        Assert.True(parsed);
        Assert.Equal(Path.GetFullPath(path), snapshotPath);
    }

    [Fact]
    public void TryParseAssociatedFileRejectsExtraOrRelativeArguments()
    {
        Assert.False(SnapshotOpenArgumentsParser.TryParseAssociatedFile(
            ["relative.replica"],
            out string? relativePath));
        Assert.False(SnapshotOpenArgumentsParser.TryParseAssociatedFile(
            ["C:\\one.replica", "C:\\two.replica"],
            out string? multiplePath));
        Assert.Null(relativePath);
        Assert.Null(multiplePath);
    }
}
