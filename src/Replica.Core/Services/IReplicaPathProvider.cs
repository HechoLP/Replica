namespace Replica.Core.Services;

public interface IReplicaPathProvider
{
    string ApplicationDataDirectory { get; }

    string LogsDirectory { get; }

    string DatabasePath { get; }

    string SnapshotsDirectory { get; }

    string RecoveryDirectory { get; }

    string RollbackDirectory { get; }

    string TemporaryDirectory { get; }
}
