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
}
