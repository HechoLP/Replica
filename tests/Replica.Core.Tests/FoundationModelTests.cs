using Replica.Core.Models;
using Replica.Core.Snapshots;

namespace Replica.Core.Tests;

public sealed class FoundationModelTests
{
    [Fact]
    public void SnapshotType_DeclaresTheThreeSupportedChoices()
    {
        SnapshotType[] values = Enum.GetValues<SnapshotType>();

        Assert.Equal(
            [
                SnapshotType.Lightweight,
                SnapshotType.Recovery,
                SnapshotType.OfflineRecoveryPack,
            ],
            values);
    }

    [Fact]
    public void ReleaseRepositoryOptions_UsesOfficialRepository()
    {
        Assert.Equal("HechoLP", ReleaseRepositoryOptions.Replica.Owner);
        Assert.Equal("Replica", ReleaseRepositoryOptions.Replica.Repository);
    }
}
