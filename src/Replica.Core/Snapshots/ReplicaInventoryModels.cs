namespace Replica.Core.Snapshots;

public sealed record ReplicaMachineInfo(
    string MachineName,
    ReplicaWindowsInfo Windows,
    string Architecture,
    string Locale);

public sealed record ReplicaWindowsInfo(
    string Edition,
    string Version,
    string Build,
    string Architecture,
    string Locale,
    string TimeZone,
    IReadOnlyList<string> Capabilities);

public sealed record ReplicaApplication(
    string DisplayName,
    string? Version,
    string? Publisher,
    string? Architecture,
    string? InstallScope,
    string? InstallType,
    ReplicaPackageIdentity PackageIdentity,
    IReadOnlyList<ReplicaArtifact> Artifacts);

public sealed record ReplicaPackageIdentity(
    string? WingetPackageId,
    string? MsixPackageFamilyName,
    string? MsiProductCode);

public sealed record ReplicaEnvironmentVariable(
    string Name,
    string Value,
    string Scope);

public sealed record ReplicaPathEntry(
    string Value,
    string Scope,
    int Order);

public sealed record ReplicaEnvironmentInventory(
    IReadOnlyList<ReplicaEnvironmentVariable> Variables,
    IReadOnlyList<ReplicaPathEntry> PathEntries);

public sealed record ReplicaFontInfo(
    string FamilyName,
    string FaceName,
    string? Version,
    string? PostScriptName);

public sealed record ReplicaPluginSnapshot(
    string PluginId,
    string PluginVersion,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<ReplicaArtifact> Artifacts,
    IReadOnlyDictionary<string, string>? Values = null,
    IReadOnlyList<ReplicaPluginFile>? Files = null,
    IReadOnlyList<ReplicaExclusion>? Exclusions = null);

public sealed record ReplicaPluginFile(
    string LogicalPath,
    string Content,
    string ContentType);

public sealed record ReplicaArtifact(
    string ArchivePath,
    string DisplayName,
    long Size,
    string Sha256);

public sealed record ReplicaChecksum(
    string EntryPath,
    string Algorithm,
    string Value);
