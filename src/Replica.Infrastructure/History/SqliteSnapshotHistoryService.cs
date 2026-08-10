using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Replica.Core.History;
using Replica.Core.Matching;
using Replica.Core.Planning;
using Replica.Core.Recovery;
using Replica.Core.Services;
using Replica.Core.Snapshots;
using Replica.Infrastructure.Snapshots;

namespace Replica.Infrastructure.History;

public sealed class SqliteSnapshotHistoryService : ISnapshotHistoryService
{
    public const int CurrentSchemaVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly IReplicaPathProvider _pathProvider;
    private readonly IRecoveryWizardRuntime _recoveryRuntime;
    private readonly ISnapshotReader _snapshotReader;
    private bool _initialized;

    public SqliteSnapshotHistoryService(
        IReplicaPathProvider pathProvider,
        ISnapshotReader snapshotReader,
        IRecoveryWizardRuntime recoveryRuntime)
    {
        _pathProvider = pathProvider;
        _snapshotReader = snapshotReader;
        _recoveryRuntime = recoveryRuntime;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            string? directory = Path.GetDirectoryName(Path.GetFullPath(_pathProvider.DatabasePath));
            if (directory is null)
            {
                throw new InvalidOperationException("The Replica database path is invalid.");
            }

            Directory.CreateDirectory(directory);
            if (SnapshotPathValidator.ContainsReparsePoint(directory) ||
                File.Exists(_pathProvider.DatabasePath) &&
                SnapshotPathValidator.ContainsReparsePoint(_pathProvider.DatabasePath))
            {
                throw new InvalidDataException("The Replica database path contains a reparse point.");
            }

            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            int version = await GetSchemaVersionAsync(connection, cancellationToken).ConfigureAwait(false);
            if (version > CurrentSchemaVersion)
            {
                throw new InvalidDataException("The Replica database schema is newer than this application.");
            }

            for (int next = version + 1; next <= CurrentSchemaVersion; next++)
            {
                await ApplyMigrationAsync(connection, next, cancellationToken).ConfigureAwait(false);
            }

            _initialized = true;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    public async Task<SnapshotHistoryEntry> AddSnapshotAsync(
        string snapshotPath,
        ReadOnlyMemory<char> password,
        int? environmentScore,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotPath);
        ValidateScore(environmentScore);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        string fullPath = Path.GetFullPath(snapshotPath);
        FileInfo before = new(fullPath);
        if (!before.Exists)
        {
            throw new FileNotFoundException("The snapshot file does not exist.", fullPath);
        }

        long initialLength = before.Length;
        DateTime initialWriteTime = before.LastWriteTimeUtc;
        ReplicaSnapshotReadResult snapshot = await _snapshotReader.ReadAsync(
            new ReplicaSnapshotReadRequest(fullPath, password),
            null,
            cancellationToken).ConfigureAwait(false);
        string fileHash = await ComputeFileHashAsync(fullPath, cancellationToken).ConfigureAwait(false);
        before.Refresh();
        if (!before.Exists || before.Length != initialLength || before.LastWriteTimeUtc != initialWriteTime)
        {
            throw new IOException("The snapshot changed while it was being indexed.");
        }

        SnapshotHistoryEntry entry = new(
            snapshot.Manifest.SnapshotId,
            Path.GetFileNameWithoutExtension(fullPath),
            string.Empty,
            [],
            fullPath,
            fileHash,
            snapshot.Manifest.CreatedAtUtc,
            snapshot.Manifest.SnapshotType,
            snapshot.Manifest.SourceMachineName,
            initialLength,
            snapshot.Manifest.Encryption is not null,
            environmentScore,
            true);
        IReadOnlyList<SnapshotCatalogItem> items = BuildCatalog(snapshot);

        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await UpsertSnapshotAsync(connection, transaction, entry, cancellationToken).ConfigureAwait(false);
        await DeleteCatalogAsync(connection, transaction, entry.SnapshotId, cancellationToken)
            .ConfigureAwait(false);
        foreach (SnapshotCatalogItem item in items)
        {
            await InsertCatalogItemAsync(connection, transaction, entry.SnapshotId, item, cancellationToken)
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await GetRequiredSnapshotAsync(entry.SnapshotId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SnapshotHistoryEntry>> GetSnapshotsAsync(
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = SnapshotSelectSql + " ORDER BY created_at_utc DESC, name COLLATE NOCASE;";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        List<SnapshotHistoryEntry> entries = [];
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(ReadSnapshot(reader));
        }

        return entries;
    }

    public async Task UpdateSnapshotAsync(
        Guid snapshotId,
        SnapshotHistoryUpdate update,
        CancellationToken cancellationToken)
    {
        ValidateUpdate(update);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        string[] tags = NormalizeTags(update.Tags);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE snapshots
            SET name = $name, description = $description, tags_json = $tags
            WHERE snapshot_id = $snapshot_id;
            """;
        command.Parameters.AddWithValue("$name", update.Name.Trim());
        command.Parameters.AddWithValue("$description", update.Description.Trim());
        command.Parameters.AddWithValue("$tags", JsonSerializer.Serialize(tags, JsonOptions));
        command.Parameters.AddWithValue("$snapshot_id", snapshotId.ToString("D"));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new KeyNotFoundException("The snapshot history entry does not exist.");
        }
    }

    public async Task DeleteSnapshotAsync(
        Guid snapshotId,
        bool deleteSnapshotFile,
        bool userConfirmed,
        CancellationToken cancellationToken)
    {
        if (!userConfirmed)
        {
            throw new InvalidOperationException("Snapshot deletion requires explicit confirmation.");
        }

        SnapshotHistoryEntry entry = await GetRequiredSnapshotAsync(snapshotId, cancellationToken)
            .ConfigureAwait(false);
        if (deleteSnapshotFile && File.Exists(entry.FilePath))
        {
            string fullPath = Path.GetFullPath(entry.FilePath);
            if (!Path.GetExtension(fullPath).Equals(".replica", StringComparison.OrdinalIgnoreCase) ||
                SnapshotPathValidator.ContainsReparsePoint(fullPath))
            {
                throw new InvalidDataException("The recorded Snapshot file path is unsafe.");
            }

            string currentHash = await ComputeFileHashAsync(fullPath, cancellationToken)
                .ConfigureAwait(false);
            if (!FixedHashEquals(currentHash, entry.SnapshotSha256))
            {
                throw new InvalidDataException(
                    "The Snapshot file changed after it was indexed and will not be deleted.");
            }
        }

        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM snapshots WHERE snapshot_id = $snapshot_id;";
        command.Parameters.AddWithValue("$snapshot_id", snapshotId.ToString("D"));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new KeyNotFoundException("The snapshot history entry does not exist.");
        }

        if (deleteSnapshotFile && File.Exists(entry.FilePath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(Path.GetFullPath(entry.FilePath));
        }
    }

    public async Task<SnapshotComparisonResult> CompareAsync(
        Guid fromSnapshotId,
        Guid toSnapshotId,
        CancellationToken cancellationToken)
    {
        if (fromSnapshotId == toSnapshotId)
        {
            throw new ArgumentException("Two different snapshots are required for comparison.");
        }

        SnapshotHistoryEntry from = await GetRequiredSnapshotAsync(fromSnapshotId, cancellationToken)
            .ConfigureAwait(false);
        SnapshotHistoryEntry to = await GetRequiredSnapshotAsync(toSnapshotId, cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyDictionary<CatalogKey, SnapshotCatalogItem> before = await GetCatalogAsync(
            fromSnapshotId,
            cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<CatalogKey, SnapshotCatalogItem> after = await GetCatalogAsync(
            toSnapshotId,
            cancellationToken).ConfigureAwait(false);

        List<SnapshotComparisonItem> changes = [];
        foreach (CatalogKey key in before.Keys.Union(after.Keys).OrderBy(key => key.Area).ThenBy(key => key.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool hasBefore = before.TryGetValue(key, out SnapshotCatalogItem? oldItem);
            bool hasAfter = after.TryGetValue(key, out SnapshotCatalogItem? newItem);
            if (hasBefore && hasAfter && CatalogEquals(oldItem!, newItem!))
            {
                continue;
            }

            SnapshotComparisonChangeKind kind = !hasBefore
                ? SnapshotComparisonChangeKind.Added
                : !hasAfter
                    ? SnapshotComparisonChangeKind.Removed
                    : SnapshotComparisonChangeKind.Changed;
            SnapshotCatalogItem display = newItem ?? oldItem!;
            changes.Add(new SnapshotComparisonItem(
                kind,
                key.Area,
                key.Key,
                display.DisplayName,
                oldItem?.Value,
                newItem?.Value,
                oldItem?.Size,
                newItem?.Size,
                oldItem?.LastWriteTimeUtc,
                newItem?.LastWriteTimeUtc,
                oldItem?.Sha256,
                newItem?.Sha256));
        }

        return new SnapshotComparisonResult(from, to, changes);
    }

    public async Task<PastStateRestorePlan> CreatePastStateRestorePlanAsync(
        Guid snapshotId,
        IReadOnlyList<RecoveryPathMapping> mappings,
        ReadOnlyMemory<char> password,
        CancellationToken cancellationToken)
    {
        SnapshotHistoryEntry entry = await GetRequiredSnapshotAsync(snapshotId, cancellationToken)
            .ConfigureAwait(false);
        if (!File.Exists(entry.FilePath))
        {
            throw new FileNotFoundException("The recorded snapshot file is unavailable.", entry.FilePath);
        }

        RecoveryAnalysisResult analysis = await _recoveryRuntime.AnalyzeAsync(
            entry.FilePath,
            mappings,
            password,
            cancellationToken).ConfigureAwait(false);
        if (analysis.Plan.ReviewStatus != RestorePlanReviewStatus.PendingReview)
        {
            throw new InvalidOperationException("A historical restore plan must require user review.");
        }

        return new PastStateRestorePlan(snapshotId, analysis.Plan, analysis.SimilarityBefore);
    }

    public async Task RecordRestoreAsync(
        RestoreHistoryRecord record,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO restore_history (
                session_id, snapshot_id, started_at_utc, completed_at_utc, status,
                succeeded_count, failed_count, skipped_count, similarity_before, similarity_after)
            VALUES ($session,
                (SELECT snapshot_id FROM snapshots WHERE snapshot_id = $snapshot),
                $started, $completed, $status,
                $succeeded, $failed, $skipped, $before, $after)
            ON CONFLICT(session_id) DO UPDATE SET
                snapshot_id = excluded.snapshot_id,
                completed_at_utc = excluded.completed_at_utc,
                status = excluded.status,
                succeeded_count = excluded.succeeded_count,
                failed_count = excluded.failed_count,
                skipped_count = excluded.skipped_count,
                similarity_before = excluded.similarity_before,
                similarity_after = excluded.similarity_after;
            """;
        command.Parameters.AddWithValue("$session", record.SessionId);
        command.Parameters.AddWithValue("$snapshot", DbValue(record.SnapshotId?.ToString("D")));
        command.Parameters.AddWithValue("$started", ToDatabaseTime(record.StartedAtUtc));
        command.Parameters.AddWithValue("$completed", ToDatabaseTime(record.CompletedAtUtc));
        command.Parameters.AddWithValue("$status", record.Status);
        command.Parameters.AddWithValue("$succeeded", record.SucceededCount);
        command.Parameters.AddWithValue("$failed", record.FailedCount);
        command.Parameters.AddWithValue("$skipped", record.SkippedCount);
        command.Parameters.AddWithValue("$before", DbValue(record.SimilarityBefore));
        command.Parameters.AddWithValue("$after", DbValue(record.SimilarityAfter));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordRollbackAsync(
        RollbackHistoryRecord record,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO rollback_history (session_id, completed_at_utc, status, item_count, failed_count)
            VALUES ($session, $completed, $status, $items, $failed)
            ON CONFLICT(session_id) DO UPDATE SET
                completed_at_utc = excluded.completed_at_utc,
                status = excluded.status,
                item_count = excluded.item_count,
                failed_count = excluded.failed_count;
            """;
        command.Parameters.AddWithValue("$session", record.SessionId);
        command.Parameters.AddWithValue("$completed", ToDatabaseTime(record.CompletedAtUtc));
        command.Parameters.AddWithValue("$status", record.Status);
        command.Parameters.AddWithValue("$items", record.ItemCount);
        command.Parameters.AddWithValue("$failed", record.FailedCount);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SavePackageMatchingOverrideAsync(
        PackageMatchingOverride matchingOverride,
        CancellationToken cancellationToken)
    {
        ValidateOverride(matchingOverride);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO package_matching_overrides (
                source_identity, target_package_identifier, confidence, created_at_utc, updated_at_utc)
            VALUES ($source, $target, $confidence, $created, $updated)
            ON CONFLICT(source_identity) DO UPDATE SET
                target_package_identifier = excluded.target_package_identifier,
                confidence = excluded.confidence,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$source", matchingOverride.SourceIdentity.Trim());
        command.Parameters.AddWithValue("$target", matchingOverride.TargetPackageIdentifier.Trim());
        command.Parameters.AddWithValue("$confidence", (int)matchingOverride.Confidence);
        command.Parameters.AddWithValue("$created", ToDatabaseTime(matchingOverride.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", ToDatabaseTime(matchingOverride.UpdatedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PackageMatchingOverride>> GetPackageMatchingOverridesAsync(
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT source_identity, target_package_identifier, confidence, created_at_utc, updated_at_utc
            FROM package_matching_overrides
            ORDER BY source_identity COLLATE NOCASE;
            """;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        List<PackageMatchingOverride> overrides = [];
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            overrides.Add(new PackageMatchingOverride(
                reader.GetString(0),
                reader.GetString(1),
                (ApplicationMatchConfidence)reader.GetInt32(2),
                ParseDatabaseTime(reader.GetString(3)),
                ParseDatabaseTime(reader.GetString(4))));
        }

        return overrides;
    }

    private async Task<SnapshotHistoryEntry> GetRequiredSnapshotAsync(
        Guid snapshotId,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = SnapshotSelectSql + " WHERE snapshot_id = $snapshot_id;";
        command.Parameters.AddWithValue("$snapshot_id", snapshotId.ToString("D"));
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new KeyNotFoundException("The snapshot history entry does not exist.");
        }

        return ReadSnapshot(reader);
    }

    private async Task<IReadOnlyDictionary<CatalogKey, SnapshotCatalogItem>> GetCatalogAsync(
        Guid snapshotId,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT area, item_key, display_name, item_value, size, last_write_time_utc, sha256
            FROM snapshot_items
            WHERE snapshot_id = $snapshot_id;
            """;
        command.Parameters.AddWithValue("$snapshot_id", snapshotId.ToString("D"));
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<CatalogKey, SnapshotCatalogItem> items = [];
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            SnapshotCatalogItem item = new(
                (SnapshotComparisonArea)reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.IsDBNull(5) ? null : ParseDatabaseTime(reader.GetString(5)),
                reader.IsDBNull(6) ? null : reader.GetString(6));
            items.Add(new CatalogKey(item.Area, item.Key), item);
        }

        return items;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = Path.GetFullPath(_pathProvider.DatabasePath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        };
        SqliteConnection connection = new(builder.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<int> GetSchemaVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private static async Task ApplyMigrationAsync(
        SqliteConnection connection,
        int version,
        CancellationToken cancellationToken)
    {
        string sql = version switch
        {
            1 => MigrationOne,
            2 => MigrationTwo,
            _ => throw new InvalidOperationException("Unknown Replica database migration."),
        };
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        command.CommandText = $"PRAGMA user_version = {version};";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SnapshotHistoryEntry entry,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO snapshots (
                snapshot_id, name, description, tags_json, file_path, snapshot_sha256,
                created_at_utc, snapshot_type, source_machine_name, file_size, is_encrypted,
                environment_score, last_seen_utc)
            VALUES ($id, $name, '', '[]', $path, $hash, $created, $type, $machine, $size,
                $encrypted, $score, $seen)
            ON CONFLICT(snapshot_id) DO UPDATE SET
                file_path = excluded.file_path,
                snapshot_sha256 = excluded.snapshot_sha256,
                created_at_utc = excluded.created_at_utc,
                snapshot_type = excluded.snapshot_type,
                source_machine_name = excluded.source_machine_name,
                file_size = excluded.file_size,
                is_encrypted = excluded.is_encrypted,
                environment_score = excluded.environment_score,
                last_seen_utc = excluded.last_seen_utc;
            """;
        command.Parameters.AddWithValue("$id", entry.SnapshotId.ToString("D"));
        command.Parameters.AddWithValue("$name", entry.Name);
        command.Parameters.AddWithValue("$path", entry.FilePath);
        command.Parameters.AddWithValue("$hash", entry.SnapshotSha256);
        command.Parameters.AddWithValue("$created", ToDatabaseTime(entry.CreatedAtUtc));
        command.Parameters.AddWithValue("$type", (int)entry.SnapshotType);
        command.Parameters.AddWithValue("$machine", entry.SourceMachineName);
        command.Parameters.AddWithValue("$size", entry.FileSize);
        command.Parameters.AddWithValue("$encrypted", entry.IsEncrypted ? 1 : 0);
        command.Parameters.AddWithValue("$score", DbValue(entry.EnvironmentScore));
        command.Parameters.AddWithValue("$seen", ToDatabaseTime(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteCatalogAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid snapshotId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM snapshot_items WHERE snapshot_id = $snapshot_id;";
        command.Parameters.AddWithValue("$snapshot_id", snapshotId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertCatalogItemAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid snapshotId,
        SnapshotCatalogItem item,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO snapshot_items (
                snapshot_id, area, item_key, display_name, item_value, size,
                last_write_time_utc, sha256)
            VALUES ($snapshot, $area, $key, $display, $value, $size, $modified, $hash);
            """;
        command.Parameters.AddWithValue("$snapshot", snapshotId.ToString("D"));
        command.Parameters.AddWithValue("$area", (int)item.Area);
        command.Parameters.AddWithValue("$key", item.Key);
        command.Parameters.AddWithValue("$display", item.DisplayName);
        command.Parameters.AddWithValue("$value", DbValue(item.Value));
        command.Parameters.AddWithValue("$size", DbValue(item.Size));
        command.Parameters.AddWithValue(
            "$modified",
            DbValue(item.LastWriteTimeUtc is null ? null : ToDatabaseTime(item.LastWriteTimeUtc.Value)));
        command.Parameters.AddWithValue("$hash", DbValue(item.Sha256));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<SnapshotCatalogItem> BuildCatalog(ReplicaSnapshotReadResult snapshot)
    {
        Dictionary<CatalogKey, SnapshotCatalogItem> items = [];
        foreach (ReplicaApplication application in snapshot.Inventory.Applications)
        {
            string key = GetApplicationKey(application);
            items[new CatalogKey(SnapshotComparisonArea.Application, key)] = new SnapshotCatalogItem(
                SnapshotComparisonArea.Application,
                key,
                application.DisplayName,
                application.Version,
                null,
                null,
                null);
        }

        foreach (ReplicaPluginSnapshot plugin in snapshot.Inventory.Plugins)
        {
            foreach ((string name, string value) in plugin.Values ?? new Dictionary<string, string>())
            {
                string key = $"{plugin.PluginId}:value:{name}";
                items[new CatalogKey(SnapshotComparisonArea.PluginSetting, key)] = new SnapshotCatalogItem(
                    SnapshotComparisonArea.PluginSetting,
                    key,
                    $"{plugin.PluginId} · {name}",
                    value,
                    null,
                    null,
                    null);
            }

            foreach (ReplicaArtifact artifact in plugin.Artifacts)
            {
                string key = $"{plugin.PluginId}:artifact:{artifact.ArchivePath}";
                items[new CatalogKey(SnapshotComparisonArea.PluginSetting, key)] = FromArtifact(
                    SnapshotComparisonArea.PluginSetting,
                    key,
                    $"{plugin.PluginId} · {artifact.DisplayName}",
                    artifact);
            }
        }

        foreach (ReplicaArtifact artifact in snapshot.Manifest.Metadata.Artifacts.Where(artifact =>
                     artifact.ArchivePath.StartsWith("files/", StringComparison.Ordinal)))
        {
            items[new CatalogKey(SnapshotComparisonArea.UserFile, artifact.ArchivePath)] = FromArtifact(
                SnapshotComparisonArea.UserFile,
                artifact.ArchivePath,
                artifact.DisplayName,
                artifact);
        }

        return items.Values.OrderBy(item => item.Area).ThenBy(item => item.Key).ToArray();

        static SnapshotCatalogItem FromArtifact(
            SnapshotComparisonArea area,
            string key,
            string displayName,
            ReplicaArtifact artifact) => new(
            area,
            key,
            displayName,
            null,
            artifact.Size,
            artifact.LastWriteTimeUtc,
            artifact.Sha256);
    }

    private static string GetApplicationKey(ReplicaApplication application)
    {
        ReplicaPackageIdentity identity = application.PackageIdentity;
        if (!string.IsNullOrWhiteSpace(identity.WingetPackageId))
        {
            return $"winget:{identity.WingetPackageId.ToUpperInvariant()}";
        }

        if (!string.IsNullOrWhiteSpace(identity.MsixPackageFamilyName))
        {
            return $"msix:{identity.MsixPackageFamilyName.ToUpperInvariant()}";
        }

        if (!string.IsNullOrWhiteSpace(identity.MsiProductCode))
        {
            return $"msi:{identity.MsiProductCode.ToUpperInvariant()}";
        }

        return $"name:{application.Publisher?.Trim().ToUpperInvariant()}|" +
            application.DisplayName.Trim().ToUpperInvariant();
    }

    private static SnapshotHistoryEntry ReadSnapshot(SqliteDataReader reader)
    {
        string path = reader.GetString(4);
        IReadOnlyList<string> tags;
        try
        {
            tags = JsonSerializer.Deserialize<string[]>(reader.GetString(3), JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            tags = [];
        }

        return new SnapshotHistoryEntry(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            tags,
            path,
            reader.GetString(5),
            ParseDatabaseTime(reader.GetString(6)),
            (SnapshotType)reader.GetInt32(7),
            reader.GetString(8),
            reader.GetInt64(9),
            reader.GetInt32(10) != 0,
            reader.IsDBNull(11) ? null : reader.GetInt32(11),
            File.Exists(path));
    }

    private static bool CatalogEquals(SnapshotCatalogItem first, SnapshotCatalogItem second)
    {
        return string.Equals(first.Value, second.Value, StringComparison.Ordinal) &&
            first.Size == second.Size &&
            first.LastWriteTimeUtc == second.LastWriteTimeUtc &&
            string.Equals(first.Sha256, second.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> ComputeFileHashAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)
            .ConfigureAwait(false));
    }

    private static bool FixedHashEquals(string first, string second)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(first),
                Convert.FromHexString(second));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void ValidateScore(int? score)
    {
        if (score is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(score));
        }
    }

    private static void ValidateUpdate(SnapshotHistoryUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (string.IsNullOrWhiteSpace(update.Name) || update.Name.Trim().Length > 128 ||
            update.Description is null || update.Description.Trim().Length > 2048 ||
            update.Tags is null)
        {
            throw new ArgumentException("Snapshot history details are invalid.", nameof(update));
        }

        _ = NormalizeTags(update.Tags);
    }

    private static string[] NormalizeTags(IReadOnlyList<string> tags)
    {
        string[] normalized = tags.Select(tag => tag?.Trim() ?? string.Empty)
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalized.Length > 20 || normalized.Any(tag => tag.Length > 64 || tag.Any(char.IsControl)))
        {
            throw new ArgumentException("Snapshot tags are invalid.", nameof(tags));
        }

        return normalized;
    }

    private static void ValidateOverride(PackageMatchingOverride matchingOverride)
    {
        ArgumentNullException.ThrowIfNull(matchingOverride);
        if (string.IsNullOrWhiteSpace(matchingOverride.SourceIdentity) ||
            matchingOverride.SourceIdentity.Length > 512 ||
            string.IsNullOrWhiteSpace(matchingOverride.TargetPackageIdentifier) ||
            matchingOverride.TargetPackageIdentifier.Length > 255 ||
            matchingOverride.SourceIdentity.Any(char.IsControl) ||
            matchingOverride.TargetPackageIdentifier.Any(char.IsControl) ||
            !Enum.IsDefined(matchingOverride.Confidence))
        {
            throw new ArgumentException("The package matching override is invalid.", nameof(matchingOverride));
        }
    }

    private static string ToDatabaseTime(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseDatabaseTime(string value) =>
        DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static object DbValue(object? value) => value ?? DBNull.Value;

    private const string SnapshotSelectSql = """
        SELECT snapshot_id, name, description, tags_json, file_path, snapshot_sha256,
               created_at_utc, snapshot_type, source_machine_name, file_size, is_encrypted,
               environment_score
        FROM snapshots
        """;

    private const string MigrationOne = """
        CREATE TABLE snapshots (
            snapshot_id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            description TEXT NOT NULL DEFAULT '',
            tags_json TEXT NOT NULL DEFAULT '[]',
            file_path TEXT NOT NULL,
            snapshot_sha256 TEXT NOT NULL,
            created_at_utc TEXT NOT NULL,
            snapshot_type INTEGER NOT NULL,
            source_machine_name TEXT NOT NULL,
            file_size INTEGER NOT NULL CHECK(file_size >= 0),
            is_encrypted INTEGER NOT NULL CHECK(is_encrypted IN (0, 1)),
            environment_score INTEGER NULL CHECK(environment_score BETWEEN 0 AND 100)
        );
        CREATE TABLE snapshot_items (
            snapshot_id TEXT NOT NULL,
            area INTEGER NOT NULL,
            item_key TEXT NOT NULL,
            display_name TEXT NOT NULL,
            item_value TEXT NULL,
            size INTEGER NULL CHECK(size IS NULL OR size >= 0),
            last_write_time_utc TEXT NULL,
            sha256 TEXT NULL,
            PRIMARY KEY(snapshot_id, area, item_key),
            FOREIGN KEY(snapshot_id) REFERENCES snapshots(snapshot_id) ON DELETE CASCADE
        );
        CREATE TABLE restore_history (
            session_id TEXT PRIMARY KEY,
            snapshot_id TEXT NULL,
            started_at_utc TEXT NOT NULL,
            completed_at_utc TEXT NOT NULL,
            status TEXT NOT NULL,
            succeeded_count INTEGER NOT NULL,
            failed_count INTEGER NOT NULL,
            skipped_count INTEGER NOT NULL,
            similarity_before INTEGER NULL,
            similarity_after INTEGER NULL,
            FOREIGN KEY(snapshot_id) REFERENCES snapshots(snapshot_id) ON DELETE SET NULL
        );
        CREATE TABLE rollback_history (
            session_id TEXT PRIMARY KEY,
            completed_at_utc TEXT NOT NULL,
            status TEXT NOT NULL,
            item_count INTEGER NOT NULL,
            failed_count INTEGER NOT NULL
        );
        CREATE TABLE package_matching_overrides (
            source_identity TEXT PRIMARY KEY,
            target_package_identifier TEXT NOT NULL,
            confidence INTEGER NOT NULL,
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL
        );
        CREATE INDEX ix_snapshots_created_at ON snapshots(created_at_utc DESC);
        CREATE INDEX ix_snapshot_items_lookup ON snapshot_items(snapshot_id, area);
        """;

    private const string MigrationTwo = """
        ALTER TABLE snapshots ADD COLUMN last_seen_utc TEXT NULL;
        UPDATE snapshots SET last_seen_utc = created_at_utc WHERE last_seen_utc IS NULL;
        """;

    private sealed record CatalogKey(SnapshotComparisonArea Area, string Key);

    private sealed record SnapshotCatalogItem(
        SnapshotComparisonArea Area,
        string Key,
        string DisplayName,
        string? Value,
        long? Size,
        DateTimeOffset? LastWriteTimeUtc,
        string? Sha256);
}
