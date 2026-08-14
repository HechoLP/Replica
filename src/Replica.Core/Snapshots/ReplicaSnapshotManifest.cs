namespace Replica.Core.Snapshots;

public sealed record ReplicaSnapshotManifest(
    string SchemaVersion,
    string ProductVersion,
    Guid SnapshotId,
    SnapshotType SnapshotType,
    DateTimeOffset CreatedAtUtc,
    string SourceMachineName,
    string WindowsVersion,
    string Architecture,
    string Locale,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<ReplicaExclusion> Exclusions,
    ReplicaSnapshotMetadata Metadata,
    ReplicaSnapshotEncryptionInfo? Encryption = null,
    ReplicaPlatformFamily SourcePlatform = ReplicaPlatformFamily.Windows)
{
    public const string LegacySchemaVersion = "1.0";
    public const string CurrentSchemaVersion = "1.1";
}

public sealed record ReplicaSnapshotMetadata(
    ReplicaMachineInfo Machine,
    IReadOnlyList<ReplicaArtifact> Artifacts,
    Replica.Core.Recovery.ReplicaHardwareInfo? Hardware = null);

public enum ReplicaPlatformFamily
{
    Windows,
    MacOS,
}

public sealed record ReplicaSnapshotEncryptionInfo(
    string Algorithm,
    string KeyDerivation,
    int Iterations,
    string Salt,
    string HeaderNonce,
    string HeaderAuthenticationTag,
    int ChunkSize);
