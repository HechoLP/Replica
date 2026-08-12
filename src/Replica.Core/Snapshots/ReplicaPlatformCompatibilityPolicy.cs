namespace Replica.Core.Snapshots;

public static class ReplicaPlatformCompatibilityPolicy
{
    public static bool CanExecuteRestore(
        ReplicaPlatformFamily sourcePlatform,
        ReplicaPlatformFamily targetPlatform)
    {
        return sourcePlatform == targetPlatform && targetPlatform == ReplicaPlatformFamily.Windows;
    }

    public static void EnsureRestoreSupported(
        ReplicaPlatformFamily sourcePlatform,
        ReplicaPlatformFamily targetPlatform)
    {
        if (!CanExecuteRestore(sourcePlatform, targetPlatform))
        {
            throw new ReplicaSnapshotException(
                "This snapshot can be inspected, but restore execution is not supported for its platform.");
        }
    }
}
