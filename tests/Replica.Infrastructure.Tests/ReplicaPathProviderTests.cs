using Replica.Infrastructure.Paths;

namespace Replica.Infrastructure.Tests;

public sealed class ReplicaPathProviderTests
{
    [Fact]
    public void Constructor_ComposesAllPathsBelowReplicaApplicationDirectory()
    {
        string localData = Path.Combine(Path.GetTempPath(), "ReplicaPathProviderTests");

        ReplicaPathProvider provider = new(localData);

        string expectedRoot = Path.Combine(Path.GetFullPath(localData), "Replica");
        Assert.Equal(expectedRoot, provider.ApplicationDataDirectory);
        Assert.Equal(Path.Combine(expectedRoot, "Logs"), provider.LogsDirectory);
        Assert.Equal(Path.Combine(expectedRoot, "replica.db"), provider.DatabasePath);
        Assert.Equal(Path.Combine(expectedRoot, "Snapshots"), provider.SnapshotsDirectory);
        Assert.Equal(Path.Combine(expectedRoot, "Recovery"), provider.RecoveryDirectory);
        Assert.Equal(Path.Combine(expectedRoot, "Rollback"), provider.RollbackDirectory);
        Assert.Equal(Path.Combine(expectedRoot, "Temp"), provider.TemporaryDirectory);
    }

    [Fact]
    public void Constructor_DoesNotCreateDirectories()
    {
        string localData = Path.Combine(
            Path.GetTempPath(),
            $"ReplicaPathProviderTests-{Guid.NewGuid():N}");

        _ = new ReplicaPathProvider(localData);

        Assert.False(Directory.Exists(localData));
    }
}
