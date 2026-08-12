using Replica.Core.Snapshots;

namespace Replica.Core.Tests;

public sealed class ReplicaPlatformCompatibilityPolicyTests
{
    [Fact]
    public void WindowsSnapshotCanUseTheWindowsRestorePipeline()
    {
        Assert.True(ReplicaPlatformCompatibilityPolicy.CanExecuteRestore(
            ReplicaPlatformFamily.Windows,
            ReplicaPlatformFamily.Windows));
    }

    [Theory]
    [InlineData(ReplicaPlatformFamily.MacOS, ReplicaPlatformFamily.Windows)]
    [InlineData(ReplicaPlatformFamily.Windows, ReplicaPlatformFamily.MacOS)]
    [InlineData(ReplicaPlatformFamily.MacOS, ReplicaPlatformFamily.MacOS)]
    public void UnsupportedPlatformPairsNeverExecuteRestore(
        ReplicaPlatformFamily source,
        ReplicaPlatformFamily target)
    {
        Assert.False(ReplicaPlatformCompatibilityPolicy.CanExecuteRestore(source, target));
        Assert.Throws<ReplicaSnapshotException>(() =>
            ReplicaPlatformCompatibilityPolicy.EnsureRestoreSupported(source, target));
    }
}
