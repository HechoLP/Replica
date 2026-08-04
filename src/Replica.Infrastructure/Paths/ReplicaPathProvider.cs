using Replica.Core.Services;

namespace Replica.Infrastructure.Paths;

public sealed class ReplicaPathProvider : IReplicaPathProvider
{
    public ReplicaPathProvider()
        : this(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData))
    {
    }

    public ReplicaPathProvider(string localApplicationDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationDataDirectory);

        string root = Path.GetFullPath(localApplicationDataDirectory);
        ApplicationDataDirectory = Path.Combine(root, "Replica");
        LogsDirectory = Path.Combine(ApplicationDataDirectory, "Logs");
        DatabasePath = Path.Combine(ApplicationDataDirectory, "replica.db");
        SnapshotsDirectory = Path.Combine(ApplicationDataDirectory, "Snapshots");
        RecoveryDirectory = Path.Combine(ApplicationDataDirectory, "Recovery");
        RollbackDirectory = Path.Combine(ApplicationDataDirectory, "Rollback");
        TemporaryDirectory = Path.Combine(ApplicationDataDirectory, "Temp");
    }

    public string ApplicationDataDirectory { get; }

    public string LogsDirectory { get; }

    public string DatabasePath { get; }

    public string SnapshotsDirectory { get; }

    public string RecoveryDirectory { get; }

    public string RollbackDirectory { get; }

    public string TemporaryDirectory { get; }
}
