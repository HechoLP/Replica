using Microsoft.Data.Sqlite;
using Replica.Core.Diffing;
using Replica.Core.Execution;
using Replica.Core.History;
using Replica.Core.Planning;
using Replica.Core.Recovery;
using Replica.Core.Services;
using Replica.Core.Snapshots;
using Replica.Infrastructure.History;
using Replica.Infrastructure.Paths;
using Replica.Infrastructure.Tests.Snapshots;

namespace Replica.Infrastructure.Tests.History;

public sealed class SqliteSnapshotHistoryServiceTests : IDisposable
{
    private readonly SnapshotTestContext _snapshots = new();
    private readonly string _storageRoot = Path.Combine(
        Path.GetTempPath(),
        "Replica.History.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Initialize_AppliesAllDatabaseMigrations()
    {
        TestHistoryContext context = CreateContext();

        await context.Service.InitializeAsync(default);

        await using SqliteConnection connection = new($"Data Source={context.Paths.DatabasePath}");
        await connection.OpenAsync();
        await using SqliteCommand version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Assert.Equal(
            SqliteSnapshotHistoryService.CurrentSchemaVersion,
            Convert.ToInt32(await version.ExecuteScalarAsync()));
        await using SqliteCommand column = connection.CreateCommand();
        column.CommandText = "SELECT COUNT(*) FROM pragma_table_info('snapshots') WHERE name = 'last_seen_utc';";
        Assert.Equal(1L, await column.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Initialize_RejectsCorruptDatabaseWithoutReplacingIt()
    {
        TestHistoryContext context = CreateContext();
        byte[] corrupt = "not-a-sqlite-database"u8.ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(context.Paths.DatabasePath)!);
        await File.WriteAllBytesAsync(context.Paths.DatabasePath, corrupt);

        await Assert.ThrowsAsync<SqliteException>(() => context.Service.InitializeAsync(default));
        SqliteConnection.ClearAllPools();

        Assert.Equal(corrupt, await File.ReadAllBytesAsync(context.Paths.DatabasePath));
    }

    [Fact]
    public async Task AddSnapshot_StoresMetadataAndComparisonIndexButNotArchiveBody()
    {
        TestHistoryContext context = CreateContext();
        string path = Path.Combine(_snapshots.RootPath, "lightweight.replica");
        await _snapshots.CreateWriter().WriteAsync(
            _snapshots.CreateRequest(path, SnapshotType.Lightweight),
            null,
            default);

        SnapshotHistoryEntry added = await context.Service.AddSnapshotAsync(
            path, ReadOnlyMemory<char>.Empty, 82, default);
        IReadOnlyList<SnapshotHistoryEntry> history = await context.Service.GetSnapshotsAsync(default);

        SnapshotHistoryEntry entry = Assert.Single(history);
        Assert.Equal(added.SnapshotId, entry.SnapshotId);
        Assert.Equal(SnapshotType.Lightweight, entry.SnapshotType);
        Assert.Equal("TEST-MACHINE", entry.SourceMachineName);
        Assert.Equal(82, entry.EnvironmentScore);
        Assert.True(entry.FileExists);
        Assert.Equal(64, entry.SnapshotSha256.Length);
        await using SqliteConnection connection = new($"Data Source={context.Paths.DatabasePath}");
        await connection.OpenAsync();
        Assert.True(await CountAsync(connection, "snapshot_items") > 0);
    }

    [Fact]
    public async Task UpdateAndDeleteSnapshot_PreservesFileWhenOnlyHistoryIsDeleted()
    {
        TestHistoryContext context = CreateContext();
        string path = await CreateSnapshotAsync("editable.replica", SnapshotType.Lightweight);
        SnapshotHistoryEntry added = await context.Service.AddSnapshotAsync(
            path, ReadOnlyMemory<char>.Empty, null, default);
        await context.Service.UpdateSnapshotAsync(
            added.SnapshotId,
            new SnapshotHistoryUpdate("Workstation", "Before upgrade", ["dev", "stable", "DEV"]),
            default);

        SnapshotHistoryEntry updated = Assert.Single(await context.Service.GetSnapshotsAsync(default));
        Assert.Equal("Workstation", updated.Name);
        Assert.Equal("Before upgrade", updated.Description);
        Assert.Equal(["dev", "stable"], updated.Tags);
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Service.DeleteSnapshotAsync(
            added.SnapshotId, false, false, default));

        await context.Service.DeleteSnapshotAsync(added.SnapshotId, false, true, default);

        Assert.Empty(await context.Service.GetSnapshotsAsync(default));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task GetSnapshots_ReportsWhenSnapshotFileDisappears()
    {
        TestHistoryContext context = CreateContext();
        string path = await CreateSnapshotAsync("missing.replica", SnapshotType.Lightweight);
        await context.Service.AddSnapshotAsync(path, ReadOnlyMemory<char>.Empty, null, default);
        File.Delete(path);

        SnapshotHistoryEntry entry = Assert.Single(await context.Service.GetSnapshotsAsync(default));

        Assert.False(entry.FileExists);
    }

    [Fact]
    public async Task DeleteSnapshotFile_RequiresMatchingIndexedHash()
    {
        TestHistoryContext context = CreateContext();
        string path = await CreateSnapshotAsync("delete-file.replica", SnapshotType.Lightweight);
        SnapshotHistoryEntry entry = await context.Service.AddSnapshotAsync(
            path, ReadOnlyMemory<char>.Empty, null, default);
        await File.AppendAllTextAsync(path, "changed-after-index");

        await Assert.ThrowsAsync<InvalidDataException>(() => context.Service.DeleteSnapshotAsync(
            entry.SnapshotId, true, true, default));

        Assert.True(File.Exists(path));
        Assert.Single(await context.Service.GetSnapshotsAsync(default));
    }

    [Fact]
    public async Task DeleteSnapshotFile_RemovesExactValidatedArchiveAndHistory()
    {
        TestHistoryContext context = CreateContext();
        string path = await CreateSnapshotAsync("delete-exact.replica", SnapshotType.Lightweight);
        SnapshotHistoryEntry entry = await context.Service.AddSnapshotAsync(
            path, ReadOnlyMemory<char>.Empty, null, default);

        await context.Service.DeleteSnapshotAsync(entry.SnapshotId, true, true, default);

        Assert.False(File.Exists(path));
        Assert.Empty(await context.Service.GetSnapshotsAsync(default));
    }

    [Fact]
    public async Task CompareSnapshots_ReportsAddedChangedAndRemovedApplications()
    {
        TestHistoryContext context = CreateContext();
        (string beforePath, string afterPath) = await CreateComparisonSnapshotsAsync(
            SnapshotType.Lightweight,
            includeSelectedFile: false);
        SnapshotHistoryEntry before = await context.Service.AddSnapshotAsync(
            beforePath, ReadOnlyMemory<char>.Empty, null, default);
        SnapshotHistoryEntry after = await context.Service.AddSnapshotAsync(
            afterPath, ReadOnlyMemory<char>.Empty, null, default);

        SnapshotComparisonResult result = await context.Service.CompareAsync(
            before.SnapshotId,
            after.SnapshotId,
            default);

        Assert.Contains(result.Items, item => item.DisplayName == "Cursor" &&
            item.ChangeKind == SnapshotComparisonChangeKind.Added);
        Assert.Contains(result.Items, item => item.DisplayName == "Node.js" &&
            item.ChangeKind == SnapshotComparisonChangeKind.Added);
        Assert.Contains(result.Items, item => item.DisplayName == "Python" &&
            item.ChangeKind == SnapshotComparisonChangeKind.Removed);
        Assert.Contains(result.Items, item => item.DisplayName == "VS Code" &&
            item.ChangeKind == SnapshotComparisonChangeKind.Changed);
        Assert.Contains(result.Items, item => item.Area == SnapshotComparisonArea.PluginSetting &&
            item.ChangeKind == SnapshotComparisonChangeKind.Changed);
    }

    [Fact]
    public async Task CompareRecoverySnapshots_UsesStoredSizeTimestampAndHashForUserFiles()
    {
        TestHistoryContext context = CreateContext();
        (string beforePath, string afterPath) = await CreateComparisonSnapshotsAsync(
            SnapshotType.Recovery,
            includeSelectedFile: true);
        SnapshotHistoryEntry before = await context.Service.AddSnapshotAsync(
            beforePath, ReadOnlyMemory<char>.Empty, null, default);
        SnapshotHistoryEntry after = await context.Service.AddSnapshotAsync(
            afterPath, ReadOnlyMemory<char>.Empty, null, default);

        SnapshotComparisonResult result = await context.Service.CompareAsync(
            before.SnapshotId,
            after.SnapshotId,
            default);

        SnapshotComparisonItem file = Assert.Single(
            result.Items,
            item => item.Area == SnapshotComparisonArea.UserFile);
        Assert.Equal(SnapshotComparisonChangeKind.Changed, file.ChangeKind);
        Assert.NotEqual(file.BeforeSize, file.AfterSize);
        Assert.NotEqual(file.BeforeSha256, file.AfterSha256);
        Assert.NotNull(file.BeforeModifiedAtUtc);
        Assert.NotNull(file.AfterModifiedAtUtc);
    }

    [Fact]
    public async Task CreatePastStateRestorePlan_ReturnsPendingReviewPlanOnly()
    {
        TestHistoryContext context = CreateContext();
        string path = await CreateSnapshotAsync("past.replica", SnapshotType.Lightweight);
        SnapshotHistoryEntry entry = await context.Service.AddSnapshotAsync(
            path, ReadOnlyMemory<char>.Empty, 75, default);

        PastStateRestorePlan result = await context.Service.CreatePastStateRestorePlanAsync(
            entry.SnapshotId,
            [],
            ReadOnlyMemory<char>.Empty,
            default);

        Assert.Equal(RestorePlanReviewStatus.PendingReview, result.Plan.ReviewStatus);
        Assert.False(result.Plan.IsApproved);
        Assert.Equal(path, context.Runtime.LastAnalyzedPath);
    }

    [Fact]
    public async Task AuditRecordsAndPackageOverride_ArePersistedAsMetadata()
    {
        TestHistoryContext context = CreateContext();
        await context.Service.RecordRestoreAsync(
            new RestoreHistoryRecord("restore-1", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                "Completed", 3, 1, 2, 70, 91),
            default);
        await context.Service.RecordRollbackAsync(
            new RollbackHistoryRecord("rollback-1", DateTimeOffset.UtcNow, "RolledBack", 2, 0),
            default);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await context.Service.SavePackageMatchingOverrideAsync(
            new PackageMatchingOverride("publisher|app", "Vendor.App", Replica.Core.Matching.ApplicationMatchConfidence.High, now, now),
            default);

        PackageMatchingOverride matchingOverride = Assert.Single(
            await context.Service.GetPackageMatchingOverridesAsync(default));

        Assert.Equal("Vendor.App", matchingOverride.TargetPackageIdentifier);
        await using SqliteConnection connection = new($"Data Source={context.Paths.DatabasePath}");
        await connection.OpenAsync();
        Assert.Equal(1L, await CountAsync(connection, "restore_history"));
        Assert.Equal(1L, await CountAsync(connection, "rollback_history"));
    }

    public void Dispose()
    {
        _snapshots.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_storageRoot))
        {
            Directory.Delete(_storageRoot, recursive: true);
        }
    }

    private TestHistoryContext CreateContext()
    {
        ReplicaPathProvider paths = new(_storageRoot);
        FakeRecoveryRuntime runtime = new();
        return new TestHistoryContext(
            paths,
            runtime,
            new SqliteSnapshotHistoryService(paths, _snapshots.CreateReader(), runtime));
    }

    private async Task<string> CreateSnapshotAsync(string name, SnapshotType type)
    {
        string path = Path.Combine(_snapshots.RootPath, name);
        await _snapshots.CreateWriter().WriteAsync(_snapshots.CreateRequest(path, type), null, default);
        return path;
    }

    private async Task<(string Before, string After)> CreateComparisonSnapshotsAsync(
        SnapshotType type,
        bool includeSelectedFile)
    {
        string selected = _snapshots.CreateDirectory($"selected-{Guid.NewGuid():N}");
        string selectedFile = Path.Combine(selected, "save.json");
        await File.WriteAllTextAsync(selectedFile, "{\"level\":1}");
        IReadOnlyList<ReplicaSelectedFolder> folders = includeSelectedFile
            ? [new ReplicaSelectedFolder(selected, "selected/project", ReplicaSelectedFolderCategory.Project)]
            : [];
        string before = Path.Combine(_snapshots.RootPath, $"before-{Guid.NewGuid():N}.replica");
        ReplicaSnapshotWriteRequest beforeRequest = WithInventory(
            _snapshots.CreateRequest(before, type, folders),
            beforeVersion: true);
        await _snapshots.CreateWriter().WriteAsync(beforeRequest, null, default);

        await Task.Delay(20);
        await File.WriteAllTextAsync(selectedFile, "{\"level\":200,\"new\":true}");
        File.SetLastWriteTimeUtc(selectedFile, DateTime.UtcNow.AddMinutes(1));
        string after = Path.Combine(_snapshots.RootPath, $"after-{Guid.NewGuid():N}.replica");
        ReplicaSnapshotWriteRequest afterRequest = WithInventory(
            _snapshots.CreateRequest(after, type, folders),
            beforeVersion: false);
        await _snapshots.CreateWriter().WriteAsync(afterRequest, null, default);
        return (before, after);
    }

    private static ReplicaSnapshotWriteRequest WithInventory(
        ReplicaSnapshotWriteRequest request,
        bool beforeVersion)
    {
        ReplicaSnapshotInventory current = request.Inventory;
        ReplicaApplication vsCode = Application("VS Code", beforeVersion ? "1.90.0" : "1.91.0", "Microsoft.VisualStudioCode");
        ReplicaApplication[] applications = beforeVersion
            ? [vsCode, Application("Python", "3.12.0", "Python.Python.3.12")]
            : [vsCode, Application("Cursor", "1.0.0", "Anysphere.Cursor"), Application("Node.js", "22.0.0", "OpenJS.NodeJS")];
        ReplicaPluginSnapshot plugin = new(
            "built-in.powertoys",
            "1.0",
            ["Settings"],
            [],
            new Dictionary<string, string>
            {
                ["FancyZones.Layout"] = beforeVersion ? "columns" : "grid",
            });
        return request with
        {
            Inventory = current with { Applications = applications, Plugins = [plugin] },
        };

        static ReplicaApplication Application(string name, string version, string packageId) => new(
            name,
            version,
            "Publisher",
            "x64",
            "User",
            "Winget",
            new ReplicaPackageIdentity(packageId, null, null),
            []);
    }

    private static async Task<long> CountAsync(SqliteConnection connection, string table)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private sealed record TestHistoryContext(
        ReplicaPathProvider Paths,
        FakeRecoveryRuntime Runtime,
        SqliteSnapshotHistoryService Service);

    private sealed class FakeRecoveryRuntime : IRecoveryWizardRuntime
    {
        public string? LastAnalyzedPath { get; private set; }

        public Task<RecoveryPreparedSnapshot> OpenSnapshotAsync(
            string snapshotPath,
            ReadOnlyMemory<char> password,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RecoveryAnalysisResult> AnalyzeAsync(
            string snapshotPath,
            IReadOnlyList<RecoveryPathMapping> mappings,
            ReadOnlyMemory<char> password,
            CancellationToken cancellationToken)
        {
            LastAnalyzedPath = snapshotPath;
            RestorePlan plan = new(
                "historical-plan",
                DiffRestoreMode.Safe,
                [],
                new RestoreDryRunSummary(0, 0, 0, 0, 0, 0, false, 0, 0, 0, TimeSpan.Zero, 0),
                RestorePlanReviewStatus.PendingReview,
                DateTimeOffset.UtcNow);
            return Task.FromResult(new RecoveryAnalysisResult(plan, 75, [], mappings, [], true, 0, 0));
        }

        public Task<RecoveryExecutionBatch> ExecuteAsync(
            string sessionId,
            string snapshotPath,
            RestorePlan approvedPlan,
            IReadOnlyList<RecoveryPathMapping> mappings,
            IReadOnlySet<string> completedActionIds,
            IReadOnlySet<string>? retryActionIds,
            ReadOnlyMemory<char> password,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RecoveryVerificationResult> VerifyAsync(
            string snapshotPath,
            IReadOnlyList<RecoveryPathMapping> mappings,
            ReadOnlyMemory<char> password,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
