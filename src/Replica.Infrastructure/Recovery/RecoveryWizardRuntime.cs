using System.Security.Cryptography;
using System.Text;
using Replica.Core.Diffing;
using Replica.Core.Execution;
using Replica.Core.Matching;
using Replica.Core.Planning;
using Replica.Core.Recovery;
using Replica.Core.Scanning;
using Replica.Core.Services;
using Replica.Core.Snapshots;
using Replica.Infrastructure.Restore;

namespace Replica.Infrastructure.Recovery;

public sealed class RecoveryWizardRuntime : IRecoveryWizardRuntime
{
    private readonly IEnvironmentDiffEngine _diffEngine;
    private readonly IEnvironmentScanner _environmentScanner;
    private readonly IRecoveryHardwareScanner _hardwareScanner;
    private readonly IReplicaPathProvider _pathProvider;
    private readonly IRestoreExecutor _restoreExecutor;
    private readonly IRestoreJournal _restoreJournal;
    private readonly IRestorePlanner _restorePlanner;
    private readonly ISnapshotReader _snapshotReader;
    private readonly RecoveryPayloadMaterializer _materializer;

    public RecoveryWizardRuntime(
        ISnapshotReader snapshotReader,
        IEnvironmentScanner environmentScanner,
        IEnvironmentDiffEngine diffEngine,
        IRestorePlanner restorePlanner,
        IRestoreExecutor restoreExecutor,
        IRestoreJournal restoreJournal,
        IRecoveryHardwareScanner hardwareScanner,
        IReplicaPathProvider pathProvider)
    {
        _snapshotReader = snapshotReader;
        _environmentScanner = environmentScanner;
        _diffEngine = diffEngine;
        _restorePlanner = restorePlanner;
        _restoreExecutor = restoreExecutor;
        _restoreJournal = restoreJournal;
        _hardwareScanner = hardwareScanner;
        _pathProvider = pathProvider;
        _materializer = new RecoveryPayloadMaterializer(pathProvider);
    }

    public async Task<RecoveryPreparedSnapshot> OpenSnapshotAsync(
        string snapshotPath,
        ReadOnlyMemory<char> password,
        CancellationToken cancellationToken)
    {
        ReplicaSnapshotReadResult snapshot = await ReadSnapshotAsync(
            snapshotPath,
            password,
            cancellationToken).ConfigureAwait(false);
        return new RecoveryPreparedSnapshot(
            snapshot.Manifest.SnapshotId,
            snapshot.Manifest.SnapshotType,
            snapshot.Manifest.SourceMachineName,
            snapshot.Manifest.WindowsVersion,
            snapshot.Manifest.Architecture,
            snapshot.Manifest.Locale,
            snapshot.Manifest.CreatedAtUtc,
            snapshot.Manifest.Encryption is not null,
            BuildSuggestedMappings(snapshot));
    }

    public async Task<RecoveryAnalysisResult> AnalyzeAsync(
        string snapshotPath,
        IReadOnlyList<RecoveryPathMapping> mappings,
        ReadOnlyMemory<char> password,
        CancellationToken cancellationToken)
    {
        ReplicaSnapshotReadResult snapshot = await ReadSnapshotAsync(
            snapshotPath,
            password,
            cancellationToken).ConfigureAwait(false);
        EnvironmentScanResult current = await _environmentScanner.ScanAsync(
            null,
            cancellationToken).ConfigureAwait(false);
        ReplicaHardwareInfo currentHardware = await _hardwareScanner.ScanAsync(cancellationToken)
            .ConfigureAwait(false);
        DiffEnvironmentState source = await BuildSourceStateAsync(
            snapshot,
            mappings,
            cancellationToken).ConfigureAwait(false);
        DiffEnvironmentState target = await BuildTargetStateAsync(
            snapshot,
            current,
            mappings,
            cancellationToken).ConfigureAwait(false);
        EnvironmentDiffResult diff = _diffEngine.Compare(
            source,
            target,
            DiffRestoreMode.Recommended,
            cancellationToken);
        RestorePlan plan = _restorePlanner.CreatePlan(diff, cancellationToken: cancellationToken);
        (bool sufficient, long required, long available) = CheckStorage(mappings);
        return new RecoveryAnalysisResult(
            plan,
            diff.Similarity.OverallScore,
            CompareHardware(snapshot.Manifest.Metadata.Hardware, currentHardware),
            mappings,
            BuildManualActions(snapshot),
            sufficient,
            required,
            available);
    }

    public async Task<RecoveryExecutionBatch> ExecuteAsync(
        string sessionId,
        string snapshotPath,
        RestorePlan approvedPlan,
        IReadOnlyList<RecoveryPathMapping> mappings,
        IReadOnlySet<string> completedActionIds,
        IReadOnlySet<string>? retryActionIds,
        ReadOnlyMemory<char> password,
        CancellationToken cancellationToken)
    {
        ReplicaSnapshotReadResult snapshot = await ReadSnapshotAsync(
            snapshotPath,
            password,
            cancellationToken).ConfigureAwait(false);
        RestorePlan remaining = BuildRemainingPlan(
            approvedPlan,
            completedActionIds,
            retryActionIds);
        if (remaining.Actions.Count == 0)
        {
            return new RecoveryExecutionBatch([], false, false);
        }

        MaterializedRecoveryPayload payload = await _materializer.MaterializeAsync(
            sessionId,
            snapshotPath,
            snapshot,
            mappings,
            password,
            cancellationToken).ConfigureAwait(false);
        Dictionary<string, FileRestoreRequest> requests = [];
        foreach (RestoreAction action in remaining.Actions.Where(action =>
                     action.Type == RestoreActionType.RestoreSelectedUserFile))
        {
            if (!payload.Files.TryGetValue(
                    action.SourceDiffKey,
                    out MaterializedRecoveryFile? file))
            {
                continue;
            }

            requests[action.Id] = new FileRestoreRequest(
                payload.RootPath,
                file.MaterializedRelativePath,
                file.DestinationPath,
                [file.ApprovedRoot],
                file.Sha256,
                Math.Max(file.Size, 1),
                true,
                file.ConflictBehavior,
                _pathProvider.RollbackDirectory);
        }

        RestoreExecutionContext context = new(
            sessionId,
            isElevated: false,
            _restoreJournal,
            requests);
        RestoreExecutionResult result = await _restoreExecutor.ExecuteAsync(
            remaining,
            context,
            new NullRestoreProgressReporter(),
            cancellationToken).ConfigureAwait(false);
        return new RecoveryExecutionBatch(
            result.Actions,
            result.RequiresRestart,
            result.WasCancelled);
    }

    public async Task<RecoveryVerificationResult> VerifyAsync(
        string snapshotPath,
        IReadOnlyList<RecoveryPathMapping> mappings,
        ReadOnlyMemory<char> password,
        CancellationToken cancellationToken)
    {
        RecoveryAnalysisResult analysis = await AnalyzeAsync(
            snapshotPath,
            mappings,
            password,
            cancellationToken).ConfigureAwait(false);
        return new RecoveryVerificationResult(
            analysis.SimilarityBefore,
            analysis.ManualActions);
    }

    private Task<ReplicaSnapshotReadResult> ReadSnapshotAsync(
        string snapshotPath,
        ReadOnlyMemory<char> password,
        CancellationToken cancellationToken)
    {
        return _snapshotReader.ReadAsync(
            new ReplicaSnapshotReadRequest(snapshotPath, password),
            null,
            cancellationToken);
    }

    private static IReadOnlyList<RecoveryPathMapping> BuildSuggestedMappings(
        ReplicaSnapshotReadResult snapshot)
    {
        return snapshot.Recovery.SelectedFolders.Select(folder =>
        {
            string prefix = $"files/{folder.ArchivePath.Replace('\\', '/').Trim('/')}/";
            long bytes = snapshot.Manifest.Metadata.Artifacts
                .Where(artifact => artifact.ArchivePath.StartsWith(prefix, StringComparison.Ordinal))
                .Sum(artifact => artifact.Size);
            return new RecoveryPathMapping(
                Path.GetFullPath(folder.SourcePath),
                Path.GetFullPath(folder.SourcePath),
                folder.Category switch
                {
                    ReplicaSelectedFolderCategory.ApplicationSettings => RecoveryPathMappingScope.ApplicationSetting,
                    ReplicaSelectedFolderCategory.GameSave => RecoveryPathMappingScope.GameSave,
                    ReplicaSelectedFolderCategory.Project => RecoveryPathMappingScope.Project,
                    _ => RecoveryPathMappingScope.SelectedFolder,
                },
                RestoreFileConflictBehavior.RenameAndKeepBoth,
                bytes);
        }).ToArray();
    }

    private static async Task<DiffEnvironmentState> BuildSourceStateAsync(
        ReplicaSnapshotReadResult snapshot,
        IReadOnlyList<RecoveryPathMapping> mappings,
        CancellationToken cancellationToken)
    {
        List<DiffApplicationEntry> applications = [];
        List<DiffApplicationEntry> storeApplications = [];
        foreach (ReplicaApplication application in snapshot.Inventory.Applications)
        {
            DiffApplicationEntry entry = new(
                ApplicationDescriptor.FromSnapshotApplication(application),
                CanRestoreAutomatically: application.PackageIdentity.WingetPackageId is not null);
            if (application.PackageIdentity.MsixPackageFamilyName is null)
            {
                applications.Add(entry);
            }
            else
            {
                storeApplications.Add(entry);
            }
        }

        List<DiffValueEntry> values = [];
        values.AddRange(snapshot.Inventory.Environment.Variables.Select(variable => new DiffValueEntry(
            DiffArea.EnvironmentVariables,
            $"{variable.Scope}:{variable.Name}",
            variable.Name,
            variable.Value,
            CanRestoreAutomatically: true)));
        AddWindowsValues(values, snapshot.Inventory.Windows);
        values.AddRange(snapshot.Inventory.Fonts.Select(font => new DiffValueEntry(
            DiffArea.Fonts,
            $"{font.FamilyName}:{font.FaceName}",
            font.FamilyName,
            font.Version,
            CanRestoreAutomatically: false)));
        foreach (ReplicaPluginSnapshot plugin in snapshot.Inventory.Plugins)
        {
            foreach ((string key, string value) in plugin.Values ??
                         new Dictionary<string, string>())
            {
                values.Add(new DiffValueEntry(
                    DiffArea.PluginSettings,
                    $"{plugin.PluginId}:{key}",
                    key,
                    value,
                    CanRestoreAutomatically: false));
            }
        }

        Dictionary<string, ReplicaChecksum> checksums = snapshot.Checksums.ToDictionary(
            checksum => checksum.EntryPath,
            StringComparer.Ordinal);
        foreach (string entryPath in snapshot.EntryPaths.Where(path =>
                     path.StartsWith("files/", StringComparison.Ordinal)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (RecoveryPayloadMaterializer.ResolveDestination(
                    entryPath,
                    snapshot.Recovery.SelectedFolders,
                    mappings) is not null &&
                checksums.TryGetValue(entryPath, out ReplicaChecksum? checksum))
            {
                values.Add(new DiffValueEntry(
                    DiffArea.SelectedUserFiles,
                    entryPath,
                    Path.GetFileName(entryPath),
                    Hash: checksum.Value,
                    CanRestoreAutomatically: true,
                    Risk: DiffRiskLevel.Medium));
            }
        }

        return new DiffEnvironmentState(
            applications,
            storeApplications,
            values,
            snapshot.Inventory.Environment.PathEntries.Select(path => new DiffPathEntry(
                path.Value,
                path.Scope,
                path.Order)).ToArray());
    }

    private static async Task<DiffEnvironmentState> BuildTargetStateAsync(
        ReplicaSnapshotReadResult snapshot,
        EnvironmentScanResult scan,
        IReadOnlyList<RecoveryPathMapping> mappings,
        CancellationToken cancellationToken)
    {
        List<DiffApplicationEntry> applications = [];
        List<DiffApplicationEntry> storeApplications = [];
        foreach (ScannedApplication application in scan.Applications)
        {
            DiffApplicationEntry entry = new(
                ApplicationDescriptor.FromScannedApplication(application),
                CanRestoreAutomatically: application.IsRestorable);
            if (application.PackageFamilyName is null)
            {
                applications.Add(entry);
            }
            else
            {
                storeApplications.Add(entry);
            }
        }

        List<DiffValueEntry> values = scan.EnvironmentVariables.Select(variable => new DiffValueEntry(
            DiffArea.EnvironmentVariables,
            $"{variable.Scope}:{variable.Name}",
            variable.Name,
            variable.Value,
            IsSensitiveExcluded: variable.IsSensitive,
            CanRestoreAutomatically: !variable.IsSensitive)).ToList();
        if (scan.Windows is not null)
        {
            values.AddRange(new[]
            {
                Value("Edition", scan.Windows.Edition),
                Value("Version", scan.Windows.Version),
                Value("Build", scan.Windows.Build),
                Value("Architecture", scan.Windows.Architecture),
                Value("Locale", scan.Windows.Locale),
                Value("TimeZone", scan.Windows.TimeZone),
            });
        }

        values.AddRange(scan.Fonts.Select(font => new DiffValueEntry(
            DiffArea.Fonts,
            $"{font.FamilyName}:{font.Style}",
            font.FamilyName,
            font.Style,
            CanRestoreAutomatically: font.IsRestorable)));

        foreach (string entryPath in snapshot.EntryPaths.Where(path =>
                     path.StartsWith("files/", StringComparison.Ordinal)))
        {
            MappedDestination? destination = RecoveryPayloadMaterializer.ResolveDestination(
                entryPath,
                snapshot.Recovery.SelectedFolders,
                mappings);
            if (destination is null || !File.Exists(destination.TargetPath))
            {
                continue;
            }

            string hash = await HashFileAsync(destination.TargetPath, cancellationToken)
                .ConfigureAwait(false);
            values.Add(new DiffValueEntry(
                DiffArea.SelectedUserFiles,
                entryPath,
                Path.GetFileName(entryPath),
                Hash: hash,
                CanRestoreAutomatically: true,
                Risk: DiffRiskLevel.Medium));
        }

        return new DiffEnvironmentState(
            applications,
            storeApplications,
            values,
            scan.PathEntries.Select(path => new DiffPathEntry(
                path.Value,
                path.Scope,
                path.Order,
                CanRestoreAutomatically: !path.IsDuplicate)).ToArray());

        static DiffValueEntry Value(string key, string value) => new(
            DiffArea.WindowsInformation,
            key,
            key,
            value,
            CanRestoreAutomatically: false);
    }

    private static void AddWindowsValues(
        ICollection<DiffValueEntry> values,
        ReplicaWindowsInfo windows)
    {
        values.Add(Value("Edition", windows.Edition));
        values.Add(Value("Version", windows.Version));
        values.Add(Value("Build", windows.Build));
        values.Add(Value("Architecture", windows.Architecture));
        values.Add(Value("Locale", windows.Locale));
        values.Add(Value("TimeZone", windows.TimeZone));

        static DiffValueEntry Value(string key, string value) => new(
            DiffArea.WindowsInformation,
            key,
            key,
            value,
            CanRestoreAutomatically: false);
    }

    private static IReadOnlyList<RecoveryHardwareDifference> CompareHardware(
        ReplicaHardwareInfo? source,
        ReplicaHardwareInfo target)
    {
        if (source is null)
        {
            return
            [
                new RecoveryHardwareDifference(
                    "Hardware",
                    "Not recorded",
                    "Current hardware detected",
                    "SourceHardwareUnavailable",
                    "Review display, audio, GPU, and drive dependent settings manually.",
                    true),
            ];
        }

        List<RecoveryHardwareDifference> differences = [];
        CompareSet("GPU", source.GraphicsAdapters, target.GraphicsAdapters,
            "Install only the current GPU's official driver; Replica never restores the old driver.", true);
        CompareSet("Audio", source.AudioDevices, target.AudioDevices,
            "Review OBS, communication, and DAW audio-device selections.", true);
        CompareSet("Drive", source.DriveRoots, target.DriveRoots,
            "Map missing drive roots before restoring files or path-dependent settings.", true);
        string sourceDisplays = string.Join(", ", source.Displays.Select(Display));
        string targetDisplays = string.Join(", ", target.Displays.Select(Display));
        if (!sourceDisplays.Equals(targetDisplays, StringComparison.OrdinalIgnoreCase))
        {
            differences.Add(new RecoveryHardwareDifference(
                "Display",
                sourceDisplays,
                targetDisplays,
                "DisplayChanged",
                "Review resolution, monitor placement, window layouts, and capture settings.",
                true));
        }

        return differences;

        void CompareSet(
            string category,
            IReadOnlyList<string> oldValues,
            IReadOnlyList<string> newValues,
            string guidance,
            bool blocks)
        {
            if (!oldValues.Order(StringComparer.OrdinalIgnoreCase)
                    .SequenceEqual(newValues.Order(StringComparer.OrdinalIgnoreCase),
                        StringComparer.OrdinalIgnoreCase))
            {
                differences.Add(new RecoveryHardwareDifference(
                    category,
                    string.Join(", ", oldValues),
                    string.Join(", ", newValues),
                    $"{category}Changed",
                    guidance,
                    blocks));
            }
        }

        static string Display(ReplicaDisplayInfo display) =>
            $"{display.Name} {display.Width}x{display.Height}";
    }

    private static IReadOnlyList<RecoveryManualAction> BuildManualActions(
        ReplicaSnapshotReadResult snapshot)
    {
        string[] applicationNames = snapshot.Inventory.Applications
            .Select(application => application.DisplayName)
            .ToArray();
        List<RecoveryManualAction> actions =
        [
            Manual("microsoft-login", "Microsoft 로그인", "Sign in through the official Microsoft account flow."),
            Manual("browser-login", "브라우저 로그인", "Sign in to browsers and resynchronize through their official flows."),
            Manual("ssh-key", "SSH Key 복원", "Restore private keys separately from a trusted private backup."),
        ];
        AddIf("steam", "Steam 로그인", "Sign in to Steam and complete any guard verification.");
        AddIf("epic", "Epic 로그인", "Sign in through the Epic Games Launcher.");
        AddIf("xbox", "Xbox 로그인", "Sign in to Xbox services through Microsoft.");
        AddIf("adobe", "Adobe 로그인", "Sign in and reactivate licensed Adobe applications.");
        AddIf("ableton", "Ableton 인증", "Authorize Ableton through its official license workflow.");
        AddIf("vst", "유료 VST 설치", "Reinstall and authorize paid VST products from their vendors.");
        AddIf("vpn", "VPN 로그인", "Reauthenticate the VPN client; credentials are never restored by Replica.");
        return actions;

        void AddIf(string token, string displayName, string guidance)
        {
            if (applicationNames.Any(name => name.Contains(token, StringComparison.OrdinalIgnoreCase)))
            {
                actions.Add(Manual(token, displayName, guidance));
            }
        }

        static RecoveryManualAction Manual(string id, string name, string guidance) => new(
            id,
            name,
            guidance,
            "AuthenticationNotRestored");
    }

    private static (bool Sufficient, long Required, long Available) CheckStorage(
        IReadOnlyList<RecoveryPathMapping> mappings)
    {
        long required = mappings.Sum(mapping => mapping.EstimatedBytes);
        long available = 0;
        HashSet<string> drives = new(StringComparer.OrdinalIgnoreCase);
        foreach (RecoveryPathMapping mapping in mappings)
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(mapping.TargetPath));
            if (root is null || !drives.Add(root))
            {
                continue;
            }

            try
            {
                DriveInfo drive = new(root);
                if (drive.IsReady)
                {
                    available = checked(available + drive.AvailableFreeSpace);
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or OverflowException)
            {
            }
        }

        return (available >= required, required, available);
    }

    private static RestorePlan BuildRemainingPlan(
        RestorePlan plan,
        IReadOnlySet<string> completedActionIds,
        IReadOnlySet<string>? retryActionIds)
    {
        RestoreAction[] candidates = plan.Actions
            .Where(action => !completedActionIds.Contains(action.Id))
            .Where(action => retryActionIds is null || retryActionIds.Contains(action.Id))
            .ToArray();
        HashSet<string> included = candidates.Select(action => action.Id)
            .ToHashSet(StringComparer.Ordinal);
        RestoreAction[] actions = candidates
            .Where(action => action.Dependencies.All(dependency =>
                completedActionIds.Contains(dependency) || included.Contains(dependency)))
            .Select(action => action with
            {
                Dependencies = action.Dependencies.Where(included.Contains).ToArray(),
            })
            .ToArray();
        return plan with { Actions = actions, ReviewStatus = RestorePlanReviewStatus.Approved };
    }

    private static async Task<string> HashFileAsync(
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
}
