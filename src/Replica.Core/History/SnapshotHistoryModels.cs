using Replica.Core.Matching;
using Replica.Core.Planning;
using Replica.Core.Snapshots;

namespace Replica.Core.History;

public sealed record SnapshotHistoryEntry(
    Guid SnapshotId,
    string Name,
    string Description,
    IReadOnlyList<string> Tags,
    string FilePath,
    string SnapshotSha256,
    DateTimeOffset CreatedAtUtc,
    SnapshotType SnapshotType,
    string SourceMachineName,
    long FileSize,
    bool IsEncrypted,
    int? EnvironmentScore,
    bool FileExists);

public sealed record SnapshotHistoryUpdate(
    string Name,
    string Description,
    IReadOnlyList<string> Tags);

public enum SnapshotComparisonChangeKind
{
    Added,
    Changed,
    Removed,
}

public enum SnapshotComparisonArea
{
    Application,
    PluginSetting,
    UserFile,
}

public sealed record SnapshotComparisonItem(
    SnapshotComparisonChangeKind ChangeKind,
    SnapshotComparisonArea Area,
    string Key,
    string DisplayName,
    string? BeforeValue,
    string? AfterValue,
    long? BeforeSize,
    long? AfterSize,
    DateTimeOffset? BeforeModifiedAtUtc,
    DateTimeOffset? AfterModifiedAtUtc,
    string? BeforeSha256,
    string? AfterSha256);

public sealed record SnapshotComparisonResult(
    SnapshotHistoryEntry From,
    SnapshotHistoryEntry To,
    IReadOnlyList<SnapshotComparisonItem> Items)
{
    public int AddedCount => Items.Count(item => item.ChangeKind == SnapshotComparisonChangeKind.Added);

    public int ChangedCount => Items.Count(item => item.ChangeKind == SnapshotComparisonChangeKind.Changed);

    public int RemovedCount => Items.Count(item => item.ChangeKind == SnapshotComparisonChangeKind.Removed);
}

public sealed record RestoreHistoryRecord(
    string SessionId,
    Guid? SnapshotId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    string Status,
    int SucceededCount,
    int FailedCount,
    int SkippedCount,
    int? SimilarityBefore,
    int? SimilarityAfter);

public sealed record RollbackHistoryRecord(
    string SessionId,
    DateTimeOffset CompletedAtUtc,
    string Status,
    int ItemCount,
    int FailedCount);

public sealed record PackageMatchingOverride(
    string SourceIdentity,
    string TargetPackageIdentifier,
    ApplicationMatchConfidence Confidence,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record PastStateRestorePlan(
    Guid SnapshotId,
    RestorePlan Plan,
    int? CurrentSimilarityScore);
