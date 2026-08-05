using Replica.Core.Recovery;

namespace Replica.Core.Snapshots;

public sealed record ReplicaSnapshotInventory(
    IReadOnlyList<ReplicaApplication> Applications,
    ReplicaEnvironmentInventory Environment,
    ReplicaWindowsInfo Windows,
    IReadOnlyList<ReplicaFontInfo> Fonts,
    IReadOnlyList<ReplicaPluginSnapshot> Plugins);

public sealed class ReplicaSnapshotEncryptionOptions
{
    public ReplicaSnapshotEncryptionOptions(ReadOnlyMemory<char> password)
    {
        Password = password;
    }

    public ReadOnlyMemory<char> Password { get; }

    public override string ToString()
    {
        return $"{nameof(ReplicaSnapshotEncryptionOptions)} {{ Password = [REDACTED] }}";
    }
}

public sealed record ReplicaSnapshotWriteRequest(
    string DestinationPath,
    SnapshotType SnapshotType,
    string ProductVersion,
    ReplicaMachineInfo Machine,
    ReplicaSnapshotInventory Inventory,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<ReplicaExclusion> Exclusions,
    ReplicaRecoveryOptions Recovery,
    ReplicaSnapshotEncryptionOptions? Encryption = null,
    ReplicaHardwareInfo? Hardware = null);

public sealed class ReplicaSnapshotReadRequest
{
    public ReplicaSnapshotReadRequest(
        string snapshotPath,
        ReadOnlyMemory<char> password = default)
    {
        SnapshotPath = snapshotPath;
        Password = password;
    }

    public string SnapshotPath { get; }

    public ReadOnlyMemory<char> Password { get; }

    public override string ToString()
    {
        return $"{nameof(ReplicaSnapshotReadRequest)} {{ SnapshotPath = [REDACTED], Password = [REDACTED] }}";
    }
}

public sealed record ReplicaSnapshotReadResult(
    ReplicaSnapshotManifest Manifest,
    ReplicaSnapshotInventory Inventory,
    ReplicaRecoveryOptions Recovery,
    IReadOnlyList<ReplicaChecksum> Checksums,
    IReadOnlyList<string> EntryPaths);

public enum ReplicaSnapshotStage
{
    Estimating,
    Staging,
    Hashing,
    Archiving,
    Validating,
    Completed,
}

public sealed record ReplicaSnapshotProgress(
    ReplicaSnapshotStage Stage,
    int CompletedItems,
    int TotalItems,
    long ProcessedBytes,
    long TotalBytes);

public sealed record ReplicaSnapshotReadLimits(
    int MaximumEntryCount,
    long MaximumEntrySize,
    long MaximumTotalUncompressedSize,
    double MaximumCompressionRatio,
    int MaximumPathDepth,
    int MaximumJsonSize)
{
    public static ReplicaSnapshotReadLimits Default { get; } = new(
        MaximumEntryCount: 10_000,
        MaximumEntrySize: 2L * 1024 * 1024 * 1024,
        MaximumTotalUncompressedSize: 10L * 1024 * 1024 * 1024,
        MaximumCompressionRatio: 200,
        MaximumPathDepth: 32,
        MaximumJsonSize: 16 * 1024 * 1024);
}

public class ReplicaSnapshotException : Exception
{
    public ReplicaSnapshotException(string message)
        : base(message)
    {
    }

    public ReplicaSnapshotException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class ReplicaSnapshotDecryptionException : ReplicaSnapshotException
{
    public ReplicaSnapshotDecryptionException()
        : base("The snapshot could not be decrypted.")
    {
    }
}
