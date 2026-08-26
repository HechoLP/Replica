using System.Globalization;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Replica.Core.Execution;
using Replica.Core.Planning;
using Replica.Core.Rollback;
using Replica.Core.Services;
using Replica.Core.Snapshots;

namespace Replica.Infrastructure.Restore;

public sealed class RollbackJournalService : IRestoreJournal, IRollbackService
{
    private const int MaximumJournalFiles = 2048;
    private const long MaximumJournalBytes = 9L * 1024 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32,
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly IEnvironmentChangeNotifier _environmentNotifier;
    private readonly IEnvironmentVariableStore _environmentStore;
    private readonly IReplicaPathProvider _pathProvider;
    private readonly IRegistryValueStore _registryStore;
    private readonly IRegistryWriteAllowList _registryWriteAllowList;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _timeProvider;

    public RollbackJournalService(
        IReplicaPathProvider pathProvider,
        IEnvironmentVariableStore environmentStore,
        IEnvironmentChangeNotifier environmentNotifier,
        IRegistryValueStore registryStore,
        IRegistryWriteAllowList registryWriteAllowList,
        TimeProvider timeProvider)
    {
        _pathProvider = pathProvider;
        _environmentStore = environmentStore;
        _environmentNotifier = environmentNotifier;
        _registryStore = registryStore;
        _registryWriteAllowList = registryWriteAllowList;
        _timeProvider = timeProvider;
    }

    public async Task RecordBeforeMutationAsync(
        RestoreJournalEntry entry,
        CancellationToken cancellationToken)
    {
        ValidateJournalEntry(entry);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string sessionDirectory = EnsureSessionStructure(entry.SessionId);
            RollbackJournalManifest manifest = File.Exists(GetManifestPath(sessionDirectory))
                ? await ReadValidatedManifestAsync(sessionDirectory, cancellationToken).ConfigureAwait(false)
                : NewManifest(entry.SessionId);
            if (manifest.Items.Any(item => item.ActionId.Equals(entry.ActionId, StringComparison.Ordinal)))
            {
                throw new IOException("A rollback journal item already exists for this action.");
            }

            RollbackJournalItem item = await CaptureItemAsync(
                sessionDirectory,
                entry.Action!,
                entry.FileRequest,
                cancellationToken).ConfigureAwait(false);
            RollbackJournalItem[] items = [.. manifest.Items, item];
            RollbackJournalManifest updated = manifest with
            {
                UpdatedAtUtc = _timeProvider.GetUtcNow(),
                State = CalculateSessionState(items),
                Items = items,
            };
            await SaveManifestAndChecksumsAsync(sessionDirectory, updated, cancellationToken)
                .ConfigureAwait(false);
            _ = await ReadValidatedManifestAsync(sessionDirectory, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MarkActionStateAsync(
        string sessionId,
        string actionId,
        RollbackJournalState state,
        string? mutationTargetPath,
        CancellationToken cancellationToken)
    {
        ValidateSessionId(sessionId);
        ValidateActionId(actionId);
        if (state is not (RollbackJournalState.Applied or RollbackJournalState.Verified or
            RollbackJournalState.RollbackPending or RollbackJournalState.RolledBack or
            RollbackJournalState.RollbackFailed))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, null);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string sessionDirectory = GetExistingSessionDirectory(sessionId);
            RollbackJournalManifest manifest = await ReadValidatedManifestAsync(
                sessionDirectory,
                cancellationToken).ConfigureAwait(false);
            int index = FindItemIndex(manifest, actionId);
            RollbackJournalItem item = manifest.Items[index];
            if (!string.IsNullOrWhiteSpace(mutationTargetPath) &&
                item.ActionType is RestoreActionType.RestoreFile or RestoreActionType.RestoreSelectedUserFile)
            {
                item = await UpdateFileTargetAsync(
                    sessionDirectory,
                    item,
                    mutationTargetPath,
                    cancellationToken).ConfigureAwait(false);
            }

            if (state == RollbackJournalState.Verified)
            {
                item = item with
                {
                    AppliedValueSha256 = await CaptureAppliedHashAsync(
                        sessionDirectory,
                        item,
                        cancellationToken).ConfigureAwait(false),
                };
            }

            item = item with { State = state };
            RollbackJournalItem[] items = manifest.Items.ToArray();
            items[index] = item;
            RollbackJournalManifest updated = manifest with
            {
                UpdatedAtUtc = _timeProvider.GetUtcNow(),
                State = CalculateSessionState(items),
                Items = items,
            };
            await SaveManifestAndChecksumsAsync(sessionDirectory, updated, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<RollbackSessionSummary>> GetRecentSessionsAsync(
        int maximumCount,
        CancellationToken cancellationToken)
    {
        if (maximumCount is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        string root = GetRollbackRoot();
        if (!Directory.Exists(root))
        {
            return [];
        }

        List<RollbackSessionSummary> sessions = [];
        foreach (string directory in Directory.EnumerateDirectories(root)
                     .OrderByDescending(Directory.GetLastWriteTimeUtc)
                     .Take(512))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                RollbackJournalManifest manifest = await ReadValidatedManifestAsync(
                    directory,
                    cancellationToken).ConfigureAwait(false);
                sessions.Add(new RollbackSessionSummary(
                    manifest.SessionId,
                    manifest.CreatedAtUtc,
                    manifest.UpdatedAtUtc,
                    manifest.State,
                    manifest.Items.Count,
                    manifest.Items.Count(item => item.CanRollbackAutomatically),
                    manifest.Items.Count(item => !item.CanRollbackAutomatically)));
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
            {
                // A corrupt session is excluded from the recent list and remains available for diagnostics.
            }
        }

        return sessions
            .OrderByDescending(session => session.UpdatedAtUtc)
            .Take(maximumCount)
            .ToArray();
    }

    public async Task<RollbackPlan> CreatePlanAsync(
        string sessionId,
        IReadOnlyCollection<string>? selectedActionIds,
        CancellationToken cancellationToken)
    {
        ValidateSessionId(sessionId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string sessionDirectory = GetExistingSessionDirectory(sessionId);
            RollbackJournalManifest manifest = await ReadValidatedManifestAsync(
                sessionDirectory,
                cancellationToken).ConfigureAwait(false);
            HashSet<string>? selected = selectedActionIds?.ToHashSet(StringComparer.Ordinal);
            if (selected is not null && selected.Any(id => manifest.Items.All(item => item.ActionId != id)))
            {
                throw new InvalidOperationException("The rollback selection contains an unknown item.");
            }

            List<RollbackPreviewItem> items = [];
            foreach (RollbackJournalItem item in manifest.Items.Reverse())
            {
                items.Add(await ToPreviewItemAsync(
                    sessionDirectory,
                    item,
                    item.CanRollbackAutomatically &&
                    item.State != RollbackJournalState.RolledBack &&
                    (selected is null || selected.Contains(item.ActionId)),
                    cancellationToken).ConfigureAwait(false));
            }

            return new RollbackPlan(
                sessionId,
                items.ToArray(),
                RollbackPlanReviewStatus.PendingReview,
                _timeProvider.GetUtcNow());
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RollbackExecutionResult> ExecuteAsync(
        RollbackPlan approvedPlan,
        IProgress<RollbackProgress>? progress,
        CancellationToken cancellationToken)
    {
        ValidateApprovedPlan(approvedPlan);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string sessionDirectory = GetExistingSessionDirectory(approvedPlan.SessionId);
            RollbackJournalManifest manifest = await ReadValidatedManifestAsync(
                sessionDirectory,
                cancellationToken).ConfigureAwait(false);
            if (manifest.State == RollbackJournalState.RolledBack)
            {
                return new RollbackExecutionResult(
                    manifest.SessionId,
                    RollbackJournalState.RolledBack,
                    [],
                    false,
                    true);
            }

            await ValidatePlanMatchesManifestAsync(
                approvedPlan,
                manifest,
                sessionDirectory,
                cancellationToken).ConfigureAwait(false);

            HashSet<string> selected = approvedPlan.Items
                .Where(item => item.IsSelected)
                .Select(item => item.ActionId)
                .ToHashSet(StringComparer.Ordinal);
            manifest = await SetSessionStateAsync(
                sessionDirectory,
                manifest,
                RollbackJournalState.RollbackPending,
                cancellationToken).ConfigureAwait(false);

            List<RollbackItemResult> results = [];
            int completed = 0;
            foreach (RollbackJournalItem item in manifest.Items.Reverse())
            {
                if (!selected.Contains(item.ActionId))
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                RollbackItemResult result;
                try
                {
                    await RollbackItemAsync(sessionDirectory, item, CancellationToken.None)
                        .ConfigureAwait(false);
                    result = new RollbackItemResult(
                        item.ActionId,
                        RollbackJournalState.RolledBack,
                        "RollbackVerified",
                        "The Replica-owned change was rolled back and verified.");
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or
                    InvalidDataException or ArgumentException or CryptographicException)
                {
                    result = new RollbackItemResult(
                        item.ActionId,
                        RollbackJournalState.RollbackFailed,
                        "RollbackItemFailed",
                        "The item could not be rolled back without risking unrelated state.");
                }

                int itemIndex = FindItemIndex(manifest, item.ActionId);
                RollbackJournalItem[] items = manifest.Items.ToArray();
                items[itemIndex] = items[itemIndex] with { State = result.State };
                manifest = manifest with
                {
                    UpdatedAtUtc = _timeProvider.GetUtcNow(),
                    Items = items,
                };
                await SaveManifestAndChecksumsAsync(sessionDirectory, manifest, CancellationToken.None)
                    .ConfigureAwait(false);
                results.Add(result);
                completed++;
                progress?.Report(new RollbackProgress(
                    manifest.SessionId,
                    item.ActionId,
                    item.Name,
                    result.State,
                    completed,
                    selected.Count,
                    result.Message));
            }

            bool failed = results.Any(result => result.State == RollbackJournalState.RollbackFailed);
            bool remaining = manifest.Items.Any(item =>
                item.CanRollbackAutomatically && item.State != RollbackJournalState.RolledBack);
            RollbackJournalState finalState = failed
                ? RollbackJournalState.RollbackFailed
                : remaining
                    ? RollbackJournalState.RollbackPending
                    : RollbackJournalState.RolledBack;
            manifest = await SetSessionStateAsync(
                sessionDirectory,
                manifest,
                finalState,
                CancellationToken.None).ConfigureAwait(false);
            return new RollbackExecutionResult(
                manifest.SessionId,
                finalState,
                results,
                failed || remaining,
                false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<RollbackJournalItem> CaptureItemAsync(
        string sessionDirectory,
        RestoreAction action,
        FileRestoreRequest? fileRequest,
        CancellationToken cancellationToken)
    {
        string key = ActionStorageKey(action.Id);
        bool plugin = action.SourceArea == Replica.Core.Diffing.DiffArea.PluginSettings;
        switch (action.Type)
        {
            case RestoreActionType.RestoreFile:
            case RestoreActionType.RestoreSelectedUserFile:
                {
                    FileRestoreRequest request = fileRequest ??
                        throw new InvalidOperationException("The file rollback mapping is missing.");
                    string destination = GetApprovedDestinationPath(
                        request.DestinationPath,
                        request.ApprovedDestinationRoots);
                    bool existed = File.Exists(destination);
                    string? backupPath = null;
                    string? originalHash = null;
                    if (existed)
                    {
                        RejectExistingReparsePoints(destination);
                        backupPath = $"files/{key}.bak";
                        string absoluteBackup = GetContainedJournalPath(sessionDirectory, backupPath);
                        originalHash = await CopyFileAndHashAsync(
                            destination,
                            absoluteBackup,
                            request.MaximumFileBytes,
                            cancellationToken).ConfigureAwait(false);
                    }

                    string dataPath = $"files/{key}.json";
                    await WriteJsonAtomicAsync(
                        GetContainedJournalPath(sessionDirectory, dataPath),
                        new RollbackFileData(
                            destination,
                            request.ApprovedDestinationRoots.Select(Path.GetFullPath).ToArray(),
                            existed,
                            originalHash,
                            request.ExpectedSha256.ToUpperInvariant()),
                        overwrite: false,
                        cancellationToken).ConfigureAwait(false);
                    return NewItem(
                        action,
                        plugin ? RollbackItemKind.Plugin : existed
                            ? RollbackItemKind.File
                            : RollbackItemKind.CreatedFile,
                        true,
                        dataPath,
                        backupPath,
                        request.ExpectedSha256.ToUpperInvariant(),
                        null);
                }
            case RestoreActionType.SetUserEnvironmentVariable:
            case RestoreActionType.SetMachineEnvironmentVariable:
            case RestoreActionType.AddPathEntry:
                {
                    bool path = action.Type == RestoreActionType.AddPathEntry;
                    EnvironmentVariableScope scope = action.Type == RestoreActionType.SetMachineEnvironmentVariable ||
                        path && action.RequiresAdministrator
                        ? EnvironmentVariableScope.Machine
                        : EnvironmentVariableScope.User;
                    string name = path
                        ? "PATH"
                        : RestoreActionKeyParser.GetEnvironmentVariableName(action, scope);
                    if (SensitiveEnvironmentPolicy.IsSensitiveName(name))
                    {
                        throw new InvalidOperationException("Sensitive environment values cannot enter the rollback journal.");
                    }

                    string? original = _environmentStore.Get(name, scope);
                    string dataPath = $"environment/{key}.json";
                    await WriteJsonAtomicAsync(
                        GetContainedJournalPath(sessionDirectory, dataPath),
                        new RollbackEnvironmentData(name, scope, original is not null, original),
                        overwrite: false,
                        cancellationToken).ConfigureAwait(false);
                    return NewItem(
                        action,
                        path ? RollbackItemKind.Path : RollbackItemKind.EnvironmentVariable,
                        true,
                        dataPath,
                        null,
                        HashNullable(GetExpectedEnvironmentValue(action, original, path)),
                        null);
                }
            case RestoreActionType.RestoreRegistryValue:
                {
                    RegistryWriteRequest request = ParseRegistryRequest(action, isElevated: true);
                    if (!_registryWriteAllowList.IsAllowed(request))
                    {
                        throw new InvalidOperationException("The registry rollback target is not allow-listed.");
                    }

                    object? original = _registryStore.GetValue(request);
                    string dataPath = $"registry/{key}.json";
                    await WriteJsonAtomicAsync(
                        GetContainedJournalPath(sessionDirectory, dataPath),
                        new RollbackRegistryData(
                            request.Hive,
                            request.KeyPath,
                            request.ValueName,
                            request.Kind,
                            original is not null,
                            SerializeRegistryValue(original, request.Kind)),
                        overwrite: false,
                        cancellationToken).ConfigureAwait(false);
                    return NewItem(
                        action,
                        plugin ? RollbackItemKind.Plugin : RollbackItemKind.Registry,
                        true,
                        dataPath,
                        null,
                        HashNullable(SerializeRegistryValue(
                            ConvertRegistryValue(action.TargetValue, request.Kind),
                            request.Kind)),
                        null);
                }
            case RestoreActionType.InstallPackage:
            case RestoreActionType.UpdatePackage:
                return NewItem(
                    action,
                    RollbackItemKind.ApplicationInstallation,
                    false,
                    null,
                    null,
                    null,
                    "Replica never removes installed applications automatically. Review this package manually.");
            default:
                return NewItem(
                    action,
                    RollbackItemKind.Plugin,
                    false,
                    null,
                    null,
                    null,
                    "This change requires the plugin's documented manual rollback procedure.");
        }
    }

    private async Task RollbackItemAsync(
        string sessionDirectory,
        RollbackJournalItem item,
        CancellationToken cancellationToken)
    {
        if (!item.CanRollbackAutomatically || item.State == RollbackJournalState.RolledBack)
        {
            throw new InvalidOperationException("The journal item is not eligible for automatic rollback.");
        }

        if (item.RequiresAdministrator && !IsAdministrator())
        {
            throw new UnauthorizedAccessException("This rollback item requires administrator rights.");
        }

        switch (item.ActionType)
        {
            case RestoreActionType.RestoreFile:
            case RestoreActionType.RestoreSelectedUserFile:
                await RollbackFileAsync(sessionDirectory, item, cancellationToken).ConfigureAwait(false);
                break;
            case RestoreActionType.SetUserEnvironmentVariable:
            case RestoreActionType.SetMachineEnvironmentVariable:
            case RestoreActionType.AddPathEntry:
                await RollbackEnvironmentAsync(sessionDirectory, item, cancellationToken).ConfigureAwait(false);
                break;
            case RestoreActionType.RestoreRegistryValue:
                await RollbackRegistryAsync(sessionDirectory, item, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException("Automatic package or unsupported plugin rollback is blocked.");
        }
    }

    private async Task RollbackFileAsync(
        string sessionDirectory,
        RollbackJournalItem item,
        CancellationToken cancellationToken)
    {
        RollbackFileData data = await ReadDataAsync<RollbackFileData>(
            sessionDirectory,
            item.DataPath,
            cancellationToken).ConfigureAwait(false);
        string destination = GetApprovedDestinationPath(data.DestinationPath, data.ApprovedDestinationRoots);
        RejectExistingReparsePoints(destination);
        if (File.Exists(destination) && item.AppliedValueSha256 is not null &&
            !FixedHashEquals(await HashFileAsync(destination, cancellationToken).ConfigureAwait(false), item.AppliedValueSha256))
        {
            throw new IOException("The restored file changed after Replica applied it.");
        }

        if (!data.ExistedBefore)
        {
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }

            return;
        }

        string backup = GetContainedJournalPath(
            sessionDirectory,
            item.BackupPath ?? throw new InvalidDataException("The file backup is missing."));
        if (!File.Exists(backup) || data.OriginalSha256 is null ||
            !FixedHashEquals(await HashFileAsync(backup, cancellationToken).ConfigureAwait(false), data.OriginalSha256))
        {
            throw new InvalidDataException("The file backup is invalid.");
        }

        string parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        RejectExistingReparsePoints(parent);
        string temporary = Path.Combine(parent, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.rollback.tmp");
        try
        {
            _ = await CopyFileAndHashAsync(backup, temporary, MaximumJournalBytes, cancellationToken)
                .ConfigureAwait(false);
            if (File.Exists(destination))
            {
                RejectExistingReparsePoints(destination);
                File.Replace(temporary, destination, null, ignoreMetadataErrors: false);
            }
            else
            {
                File.Move(temporary, destination, overwrite: false);
            }

            if (!FixedHashEquals(await HashFileAsync(destination, cancellationToken).ConfigureAwait(false), data.OriginalSha256))
            {
                throw new IOException("The rolled-back file failed verification.");
            }
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private async Task RollbackEnvironmentAsync(
        string sessionDirectory,
        RollbackJournalItem item,
        CancellationToken cancellationToken)
    {
        RollbackEnvironmentData data = await ReadDataAsync<RollbackEnvironmentData>(
            sessionDirectory,
            item.DataPath,
            cancellationToken).ConfigureAwait(false);
        string? current = _environmentStore.Get(data.Name, data.Scope);
        if (item.AppliedValueSha256 is not null && !FixedHashEquals(HashNullable(current), item.AppliedValueSha256))
        {
            throw new IOException("The environment value changed after Replica applied it.");
        }

        _environmentStore.Set(data.Name, data.ExistedBefore ? data.OriginalValue : null, data.Scope);
        _environmentNotifier.NotifyEnvironmentChanged();
        string? restored = _environmentStore.Get(data.Name, data.Scope);
        if (!string.Equals(restored, data.ExistedBefore ? data.OriginalValue : null, StringComparison.Ordinal))
        {
            throw new IOException("The environment rollback failed verification.");
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task RollbackRegistryAsync(
        string sessionDirectory,
        RollbackJournalItem item,
        CancellationToken cancellationToken)
    {
        RollbackRegistryData data = await ReadDataAsync<RollbackRegistryData>(
            sessionDirectory,
            item.DataPath,
            cancellationToken).ConfigureAwait(false);
        RegistryWriteRequest request = new(
            data.Hive,
            data.KeyPath,
            data.ValueName,
            data.Kind,
            data.OriginalValue ?? string.Empty,
            item.RequiresAdministrator);
        if (!_registryWriteAllowList.IsAllowed(request))
        {
            throw new InvalidDataException("The registry rollback target is no longer allow-listed.");
        }

        object? current = _registryStore.GetValue(request);
        if (item.AppliedValueSha256 is not null &&
            !FixedHashEquals(HashNullable(SerializeRegistryValue(current, data.Kind)), item.AppliedValueSha256))
        {
            throw new IOException("The registry value changed after Replica applied it.");
        }

        if (data.ExistedBefore)
        {
            _registryStore.SetValue(request, ConvertRegistryValue(data.OriginalValue, data.Kind));
        }
        else
        {
            _registryStore.DeleteValue(request);
        }

        object? restored = _registryStore.GetValue(request);
        if (!string.Equals(
                SerializeRegistryValue(restored, data.Kind),
                data.ExistedBefore ? data.OriginalValue : null,
                StringComparison.Ordinal))
        {
            throw new IOException("The registry rollback failed verification.");
        }
    }

    private async Task<string?> CaptureAppliedHashAsync(
        string sessionDirectory,
        RollbackJournalItem item,
        CancellationToken cancellationToken)
    {
        switch (item.ActionType)
        {
            case RestoreActionType.RestoreFile:
            case RestoreActionType.RestoreSelectedUserFile:
                {
                    RollbackFileData data = await ReadDataAsync<RollbackFileData>(
                        sessionDirectory,
                        item.DataPath,
                        cancellationToken).ConfigureAwait(false);
                    return File.Exists(data.DestinationPath)
                        ? await HashFileAsync(data.DestinationPath, cancellationToken).ConfigureAwait(false)
                        : null;
                }
            case RestoreActionType.SetUserEnvironmentVariable:
            case RestoreActionType.SetMachineEnvironmentVariable:
            case RestoreActionType.AddPathEntry:
                {
                    RollbackEnvironmentData data = await ReadDataAsync<RollbackEnvironmentData>(
                        sessionDirectory,
                        item.DataPath,
                        cancellationToken).ConfigureAwait(false);
                    return HashNullable(_environmentStore.Get(data.Name, data.Scope));
                }
            case RestoreActionType.RestoreRegistryValue:
                {
                    RollbackRegistryData data = await ReadDataAsync<RollbackRegistryData>(
                        sessionDirectory,
                        item.DataPath,
                        cancellationToken).ConfigureAwait(false);
                    RegistryWriteRequest request = new(
                        data.Hive,
                        data.KeyPath,
                        data.ValueName,
                        data.Kind,
                        string.Empty,
                        item.RequiresAdministrator);
                    return HashNullable(SerializeRegistryValue(_registryStore.GetValue(request), data.Kind));
                }
            default:
                return null;
        }
    }

    private async Task<RollbackJournalItem> UpdateFileTargetAsync(
        string sessionDirectory,
        RollbackJournalItem item,
        string mutationTargetPath,
        CancellationToken cancellationToken)
    {
        RollbackFileData data = await ReadDataAsync<RollbackFileData>(
            sessionDirectory,
            item.DataPath,
            cancellationToken).ConfigureAwait(false);
        string target = GetApprovedDestinationPath(mutationTargetPath, data.ApprovedDestinationRoots);
        if (target.Equals(data.DestinationPath, StringComparison.OrdinalIgnoreCase))
        {
            return item;
        }

        RollbackFileData updated = data with
        {
            DestinationPath = target,
            ExistedBefore = false,
            OriginalSha256 = null,
        };
        await WriteJsonAtomicAsync(
            GetContainedJournalPath(sessionDirectory, item.DataPath!),
            updated,
            overwrite: true,
            cancellationToken).ConfigureAwait(false);
        return item with
        {
            Kind = item.Kind == RollbackItemKind.Plugin ? item.Kind : RollbackItemKind.CreatedFile,
            BackupPath = null,
        };
    }

    private async Task<RollbackJournalManifest> ReadValidatedManifestAsync(
        string sessionDirectory,
        CancellationToken cancellationToken)
    {
        RejectExistingReparsePoints(sessionDirectory);
        string manifestPath = GetManifestPath(sessionDirectory);
        string checksumsPath = GetChecksumsPath(sessionDirectory);
        if (!File.Exists(manifestPath) || !File.Exists(checksumsPath))
        {
            throw new InvalidDataException("The rollback journal is incomplete.");
        }

        Dictionary<string, string> checksums = await ReadJsonFileAsync<Dictionary<string, string>>(
            checksumsPath,
            cancellationToken).ConfigureAwait(false);
        string[] files = EnumerateJournalFiles(sessionDirectory)
            .Where(path => !path.Equals(checksumsPath, StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (files.Length is < 1 or > MaximumJournalFiles || checksums.Count != files.Length)
        {
            throw new InvalidDataException("The rollback journal file set is invalid.");
        }

        long total = 0;
        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectExistingReparsePoints(file);
            total = checked(total + new FileInfo(file).Length);
            if (total > MaximumJournalBytes)
            {
                throw new InvalidDataException("The rollback journal exceeds its size limit.");
            }

            string relative = NormalizeRelativePath(Path.GetRelativePath(sessionDirectory, file));
            if (!checksums.TryGetValue(relative, out string? expected) ||
                !FixedHashEquals(await HashFileAsync(file, cancellationToken).ConfigureAwait(false), expected))
            {
                throw new InvalidDataException("The rollback journal checksum verification failed.");
            }
        }

        RollbackJournalManifest manifest = await ReadJsonFileAsync<RollbackJournalManifest>(
            manifestPath,
            cancellationToken).ConfigureAwait(false);
        ValidateManifest(manifest, Path.GetFileName(sessionDirectory));
        foreach (RollbackJournalItem item in manifest.Items)
        {
            if (item.DataPath is not null)
            {
                _ = GetContainedJournalPath(sessionDirectory, item.DataPath);
            }

            if (item.BackupPath is not null)
            {
                _ = GetContainedJournalPath(sessionDirectory, item.BackupPath);
            }
        }

        return manifest;
    }

    private async Task SaveManifestAndChecksumsAsync(
        string sessionDirectory,
        RollbackJournalManifest manifest,
        CancellationToken cancellationToken)
    {
        await WriteJsonAtomicAsync(
            GetManifestPath(sessionDirectory),
            manifest,
            overwrite: true,
            cancellationToken).ConfigureAwait(false);
        Dictionary<string, string> checksums = new(StringComparer.Ordinal);
        foreach (string file in EnumerateJournalFiles(sessionDirectory)
                     .Where(path => !path.Equals(GetChecksumsPath(sessionDirectory), StringComparison.OrdinalIgnoreCase) &&
                         !path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = NormalizeRelativePath(Path.GetRelativePath(sessionDirectory, file));
            checksums.Add(relative, await HashFileAsync(file, cancellationToken).ConfigureAwait(false));
        }

        await WriteJsonAtomicAsync(
            GetChecksumsPath(sessionDirectory),
            checksums,
            overwrite: true,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<RollbackJournalManifest> SetSessionStateAsync(
        string sessionDirectory,
        RollbackJournalManifest manifest,
        RollbackJournalState state,
        CancellationToken cancellationToken)
    {
        RollbackJournalManifest updated = manifest with
        {
            UpdatedAtUtc = _timeProvider.GetUtcNow(),
            State = state,
        };
        await SaveManifestAndChecksumsAsync(sessionDirectory, updated, cancellationToken)
            .ConfigureAwait(false);
        return updated;
    }

    private static async Task WriteJsonAtomicAsync<T>(
        string path,
        T value,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        try
        {
            await using (FileStream stream = new(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(json, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                if (!overwrite)
                {
                    throw new IOException("A rollback journal file already exists.");
                }

                File.Replace(temporary, path, null, ignoreMetadataErrors: false);
            }
            else
            {
                File.Move(temporary, path, overwrite: false);
            }
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static async Task<T> ReadDataAsync<T>(
        string sessionDirectory,
        string? relativePath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidDataException("The rollback payload path is missing.");
        }

        return await ReadJsonFileAsync<T>(
            GetContainedJournalPath(sessionDirectory, relativePath),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> ReadJsonFileAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("The rollback journal JSON is invalid.");
    }

    private string EnsureSessionStructure(string sessionId)
    {
        ValidateSessionId(sessionId);
        string root = GetRollbackRoot();
        Directory.CreateDirectory(root);
        RejectExistingReparsePoints(root);
        string session = GetContainedSessionDirectory(root, sessionId);
        Directory.CreateDirectory(session);
        ApplyRestrictedDirectoryAcl(session);
        foreach (string child in new[] { "files", "registry", "environment" })
        {
            Directory.CreateDirectory(Path.Combine(session, child));
        }

        RejectExistingReparsePoints(session);
        return session;
    }

    private string GetExistingSessionDirectory(string sessionId)
    {
        ValidateSessionId(sessionId);
        string directory = GetContainedSessionDirectory(GetRollbackRoot(), sessionId);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException("The rollback session does not exist.");
        }

        return directory;
    }

    private string GetRollbackRoot() => Path.GetFullPath(_pathProvider.RollbackDirectory);

    private static string GetContainedSessionDirectory(string root, string sessionId)
    {
        string directory = Path.GetFullPath(Path.Combine(root, sessionId));
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!directory.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The rollback session path is unsafe.");
        }

        return directory;
    }

    private static string GetContainedJournalPath(string sessionDirectory, string relativePath)
    {
        if (Path.IsPathFullyQualified(relativePath) || relativePath.Contains(':') ||
            relativePath.Replace('\\', '/').Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new InvalidDataException("The rollback journal path is unsafe.");
        }

        string full = Path.GetFullPath(Path.Combine(
            sessionDirectory,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = sessionDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The rollback journal path escaped its session.");
        }

        return full;
    }

    private static string GetApprovedDestinationPath(string path, IReadOnlyList<string> roots)
    {
        if (!Path.IsPathFullyQualified(path) || roots is null or { Count: 0 })
        {
            throw new InvalidDataException("The rollback destination is invalid.");
        }

        string full = Path.GetFullPath(path);
        string? approvedRoot = roots.Select(Path.GetFullPath).FirstOrDefault(root =>
            Directory.Exists(root) &&
            full.StartsWith(
                root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) &&
            !full.Equals(root, StringComparison.OrdinalIgnoreCase));
        if (approvedRoot is null)
        {
            throw new InvalidDataException("The rollback destination is outside its approved root.");
        }

        RejectExistingReparsePoints(approvedRoot);
        RejectExistingReparsePoints(full);

        return full;
    }

    private static async Task<string> CopyFileAndHashAsync(
        string source,
        string destination,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        RejectExistingReparsePoints(source);
        await using FileStream input = new(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using FileStream output = new(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total = checked(total + read);
            if (total > maximumBytes)
            {
                throw new IOException("The rollback file exceeds its size limit.");
            }

            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static RegistryWriteRequest ParseRegistryRequest(RestoreAction action, bool isElevated)
    {
        const string prefix = "registry:";
        if (!action.SourceDiffKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The registry action key is invalid.", nameof(action));
        }

        string[] fields = action.SourceDiffKey[prefix.Length..].Split('|');
        if (fields.Length != 3 ||
            !Enum.TryParse(fields[2], ignoreCase: true, out RegistryValueDataKind kind))
        {
            throw new ArgumentException("The registry action key is invalid.", nameof(action));
        }

        int separator = fields[0].IndexOf('\\');
        if (separator <= 0 || separator == fields[0].Length - 1)
        {
            throw new ArgumentException("The registry action path is invalid.", nameof(action));
        }

        return new RegistryWriteRequest(
            fields[0][..separator].ToUpperInvariant(),
            fields[0][(separator + 1)..],
            fields[1],
            kind,
            action.TargetValue,
            isElevated);
    }

    private static object ConvertRegistryValue(string? value, RegistryValueDataKind kind)
    {
        return kind switch
        {
            RegistryValueDataKind.String or RegistryValueDataKind.ExpandString => value ?? string.Empty,
            RegistryValueDataKind.DWord when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) => result,
            RegistryValueDataKind.QWord when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long result) => result,
            RegistryValueDataKind.MultiString => JsonSerializer.Deserialize<string[]>(value ?? "[]", JsonOptions) ?? [],
            RegistryValueDataKind.Binary => Convert.FromBase64String(value ?? string.Empty),
            _ => throw new InvalidDataException("The rollback registry value is invalid."),
        };
    }

    private static string GetExpectedEnvironmentValue(
        RestoreAction action,
        string? original,
        bool path)
    {
        string target = action.TargetValue ??
            throw new InvalidDataException("The rollback environment target is missing.");
        if (!path)
        {
            return target;
        }

        string normalizedTarget = NormalizePathEntry(target);
        string[] entries = (original ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries.Any(entry => NormalizePathEntry(entry).Equals(
                normalizedTarget,
                StringComparison.OrdinalIgnoreCase)))
        {
            return original ?? string.Empty;
        }

        return entries.Length == 0
            ? target.Trim()
            : $"{string.Join(';', entries)};{target.Trim()}";
    }

    private static string NormalizePathEntry(string value)
    {
        string trimmed = value.Trim().Trim('"');
        if (trimmed.Length == 0 || trimmed.Contains(';') || trimmed.Any(char.IsControl))
        {
            throw new InvalidDataException("The rollback PATH entry is invalid.");
        }

        string expanded = System.Environment.ExpandEnvironmentVariables(trimmed);
        if (!Path.IsPathFullyQualified(expanded))
        {
            throw new InvalidDataException("The rollback PATH entry is not fully qualified.");
        }

        return Path.GetFullPath(expanded)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string? SerializeRegistryValue(object? value, RegistryValueDataKind kind)
    {
        return value switch
        {
            null => null,
            string text => text,
            int number => number.ToString(CultureInfo.InvariantCulture),
            long number => number.ToString(CultureInfo.InvariantCulture),
            string[] strings => JsonSerializer.Serialize(strings, JsonOptions),
            byte[] bytes => Convert.ToBase64String(bytes),
            _ => throw new InvalidDataException($"Unsupported rollback registry value kind: {kind}."),
        };
    }

    private static RollbackJournalItem NewItem(
        RestoreAction action,
        RollbackItemKind kind,
        bool automatic,
        string? dataPath,
        string? backupPath,
        string? appliedHash,
        string? manualInstruction)
    {
        return new RollbackJournalItem(
            action.Id,
            action.Type,
            action.Name,
            kind,
            RollbackJournalState.Prepared,
            automatic,
            action.RequiresAdministrator,
            dataPath,
            backupPath,
            appliedHash,
            manualInstruction);
    }

    private RollbackJournalManifest NewManifest(string sessionId)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        return new RollbackJournalManifest(
            RollbackJournalManifest.CurrentSchemaVersion,
            sessionId,
            now,
            now,
            RollbackJournalState.Prepared,
            []);
    }

    private async Task<RollbackPreviewItem> ToPreviewItemAsync(
        string sessionDirectory,
        RollbackJournalItem item,
        bool selected,
        CancellationToken cancellationToken)
    {
        (string target, string effect) = item.ActionType switch
        {
            RestoreActionType.RestoreFile or RestoreActionType.RestoreSelectedUserFile =>
                DescribeFile(await ReadDataAsync<RollbackFileData>(
                    sessionDirectory,
                    item.DataPath,
                    cancellationToken).ConfigureAwait(false)),
            RestoreActionType.SetUserEnvironmentVariable or
                RestoreActionType.SetMachineEnvironmentVariable or
                RestoreActionType.AddPathEntry =>
                DescribeEnvironment(await ReadDataAsync<RollbackEnvironmentData>(
                    sessionDirectory,
                    item.DataPath,
                    cancellationToken).ConfigureAwait(false)),
            RestoreActionType.RestoreRegistryValue =>
                DescribeRegistry(await ReadDataAsync<RollbackRegistryData>(
                    sessionDirectory,
                    item.DataPath,
                    cancellationToken).ConfigureAwait(false)),
            _ => (item.Name, item.ManualInstruction ?? "수동으로 검토합니다."),
        };
        return new RollbackPreviewItem(
            item.ActionId,
            item.Name,
            item.Kind,
            item.State,
            item.CanRollbackAutomatically,
            item.RequiresAdministrator,
            selected,
            item.ManualInstruction,
            target,
            effect,
            item.AppliedValueSha256);
    }

    private static (string Target, string Effect) DescribeFile(RollbackFileData data)
    {
        return (
            data.DestinationPath,
            data.ExistedBefore
                ? "복원 전 보관 사본으로 이 파일을 되돌립니다."
                : "Replica가 새로 만든 이 파일을 삭제합니다.");
    }

    private static (string Target, string Effect) DescribeEnvironment(RollbackEnvironmentData data)
    {
        return (
            $"{data.Scope} 환경 변수 · {data.Name}",
            data.ExistedBefore
                ? "이 변수의 복원 전 값을 되살립니다."
                : "Replica가 만든 이 변수를 제거합니다.");
    }

    private static (string Target, string Effect) DescribeRegistry(RollbackRegistryData data)
    {
        return (
            $"{data.Hive}\\{data.KeyPath} · {data.ValueName}",
            data.ExistedBefore
                ? "이 레지스트리 값의 복원 전 값을 되살립니다."
                : "Replica가 만든 이 레지스트리 값을 제거합니다.");
    }

    private static RollbackJournalState CalculateSessionState(IReadOnlyList<RollbackJournalItem> items)
    {
        if (items.Any(item => item.CanRollbackAutomatically) && items.All(item =>
                !item.CanRollbackAutomatically || item.State == RollbackJournalState.RolledBack))
        {
            return RollbackJournalState.RolledBack;
        }

        if (items.Any(item => item.State == RollbackJournalState.RollbackFailed))
        {
            return RollbackJournalState.RollbackFailed;
        }

        if (items.Any(item => item.State == RollbackJournalState.RollbackPending))
        {
            return RollbackJournalState.RollbackPending;
        }

        if (items.Count > 0 && items.All(item => item.State == RollbackJournalState.Verified))
        {
            return RollbackJournalState.Verified;
        }

        return items.Any(item => item.State is RollbackJournalState.Applied or RollbackJournalState.Verified)
            ? RollbackJournalState.Applied
            : RollbackJournalState.Prepared;
    }

    private static int FindItemIndex(RollbackJournalManifest manifest, string actionId)
    {
        for (int index = 0; index < manifest.Items.Count; index++)
        {
            if (manifest.Items[index].ActionId.Equals(actionId, StringComparison.Ordinal))
            {
                return index;
            }
        }

        throw new InvalidOperationException("The rollback journal action was not found.");
    }

    private static void ValidateJournalEntry(RestoreJournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ValidateSessionId(entry.SessionId);
        ValidateActionId(entry.ActionId);
        if (entry.Action is null || entry.Action.Id != entry.ActionId || entry.Action.Type != entry.ActionType)
        {
            throw new ArgumentException("The rollback journal action is invalid.", nameof(entry));
        }
    }

    private static void ValidateManifest(RollbackJournalManifest manifest, string expectedSessionId)
    {
        if (manifest.SchemaVersion != RollbackJournalManifest.CurrentSchemaVersion ||
            !manifest.SessionId.Equals(expectedSessionId, StringComparison.Ordinal) ||
            !Enum.IsDefined(manifest.State) ||
            manifest.Items is null or { Count: > 1000 } ||
            manifest.Items.Any(item => item is null ||
                string.IsNullOrWhiteSpace(item.ActionId) ||
                string.IsNullOrWhiteSpace(item.Name) ||
                !Enum.IsDefined(item.ActionType) ||
                !Enum.IsDefined(item.Kind) ||
                !Enum.IsDefined(item.State)) ||
            manifest.Items.Select(item => item.ActionId).Distinct(StringComparer.Ordinal).Count() != manifest.Items.Count)
        {
            throw new InvalidDataException("The rollback journal manifest is invalid.");
        }
    }

    private static void ValidateApprovedPlan(RollbackPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ValidateSessionId(plan.SessionId);
        if (plan.ReviewStatus != RollbackPlanReviewStatus.Approved || plan.Items is null ||
            !plan.Items.Any(item => item.IsSelected) ||
            plan.Items.Any(item => item is null || !Enum.IsDefined(item.Kind) || !Enum.IsDefined(item.State)) ||
            plan.Items.Where(item => item.IsSelected).Any(item => !item.CanRollbackAutomatically))
        {
            throw new InvalidOperationException("Only an approved typed rollback plan can execute.");
        }
    }

    private async Task ValidatePlanMatchesManifestAsync(
        RollbackPlan plan,
        RollbackJournalManifest manifest,
        string sessionDirectory,
        CancellationToken cancellationToken)
    {
        if (plan.Items.Count != manifest.Items.Count)
        {
            throw new InvalidOperationException("The rollback plan no longer matches its journal.");
        }

        foreach (RollbackPreviewItem preview in plan.Items)
        {
            RollbackJournalItem? item = manifest.Items.FirstOrDefault(candidate => candidate.ActionId == preview.ActionId);
            RollbackPreviewItem? current = item is null
                ? null
                : await ToPreviewItemAsync(
                    sessionDirectory,
                    item,
                    preview.IsSelected,
                    cancellationToken).ConfigureAwait(false);
            if (item is null || item.Name != preview.Name || item.Kind != preview.Kind ||
                item.State != preview.State ||
                item.CanRollbackAutomatically != preview.CanRollbackAutomatically ||
                item.RequiresAdministrator != preview.RequiresAdministrator ||
                current is null || current.Target != preview.Target ||
                current.Effect != preview.Effect ||
                current.ExpectedCurrentSha256 != preview.ExpectedCurrentSha256 ||
                current.ManualInstruction != preview.ManualInstruction)
            {
                throw new InvalidOperationException("The rollback plan was modified or is stale.");
            }
        }
    }

    private static void ValidateSessionId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            value.Any(character => !char.IsLetterOrDigit(character) && character is not ('-' or '_')))
        {
            throw new ArgumentException("The rollback session identifier is invalid.", nameof(value));
        }
    }

    private static void ValidateActionId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
        {
            throw new ArgumentException("The rollback action identifier is invalid.", nameof(value));
        }
    }

    private static string ActionStorageKey(string actionId)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(actionId)))[..32].ToLowerInvariant();
    }

    private static string GetManifestPath(string sessionDirectory) => Path.Combine(sessionDirectory, "manifest.json");

    private static string GetChecksumsPath(string sessionDirectory) => Path.Combine(sessionDirectory, "checksums.json");

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/');

    private static string HashNullable(string? value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? "\u0000")));
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

    private static void RejectExistingReparsePoints(string path)
    {
        string? current = Path.GetFullPath(path);
        if (File.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The rollback path contains a reparse point.");
        }

        current = File.Exists(current) || !Directory.Exists(current)
            ? Path.GetDirectoryName(current)
            : current;
        while (!string.IsNullOrEmpty(current) && Directory.Exists(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("The rollback path contains a reparse point.");
            }

            current = Path.GetDirectoryName(current);
        }
    }

    private static IEnumerable<string> EnumerateJournalFiles(string root)
    {
        Queue<string> pending = new();
        pending.Enqueue(Path.GetFullPath(root));
        int entryCount = 0;
        while (pending.Count > 0)
        {
            string directory = pending.Dequeue();
            RejectExistingReparsePoints(directory);
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                entryCount++;
                if (entryCount > MaximumJournalFiles * 2)
                {
                    throw new InvalidDataException("The rollback journal contains too many entries.");
                }

                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("The rollback journal contains a reparse point.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Enqueue(entry);
                }
                else
                {
                    yield return entry;
                }
            }
        }
    }

    private static void ApplyRestrictedDirectoryAcl(string path)
    {
        SecurityIdentifier user = WindowsIdentity.GetCurrent().User ??
            throw new InvalidOperationException("The current Windows identity is unavailable.");
        DirectorySecurity security = new();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (SecurityIdentifier identity in new[]
                 {
                     user,
                     new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                     new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                 })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                identity,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        new DirectoryInfo(path).SetAccessControl(security);
    }

    private static bool IsAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A later bounded cleanup pass can remove an abandoned temporary file.
        }
        catch (UnauthorizedAccessException)
        {
            // Cleanup failure does not weaken journal validation.
        }
    }
}
