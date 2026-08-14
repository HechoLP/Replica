using System.Security.Cryptography;
using System.Text;
using Replica.Core.Diffing;
using Replica.Core.Execution;
using Replica.Core.Matching;
using Replica.Core.Planning;
using Replica.Core.Rollback;
using Replica.Infrastructure.Paths;
using Replica.Infrastructure.Restore;

namespace Replica.Infrastructure.Tests.Restore;

public sealed class RollbackJournalServiceTests : IDisposable
{
    private readonly FakeEnvironmentStore _environment = new();
    private readonly FakeEnvironmentNotifier _notifier = new();
    private readonly FakeRegistryStore _registry = new();
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"ReplicaRollbackTests-{Guid.NewGuid():N}");

    [Fact]
    public async Task ExecuteAsync_RestoresOriginalFile()
    {
        string destinationRoot = CreateDirectory("destination");
        string destination = WriteFile(destinationRoot, "settings.json", "original");
        FileRestoreRequest request = FileRequest(destinationRoot, destination, "restored");
        RollbackJournalService service = CreateService();
        RestoreAction action = FileAction("file-existing");
        await PrepareAsync(service, "file-session", action, request);
        File.WriteAllText(destination, "restored", Encoding.UTF8);
        await MarkVerifiedAsync(service, "file-session", action.Id, destination);

        RollbackExecutionResult result = await ExecuteAllAsync(service, "file-session");

        Assert.Equal(RollbackJournalState.RolledBack, result.State);
        Assert.Equal("original", File.ReadAllText(destination, Encoding.UTF8));
        AssertJournalShape("file-session");
    }

    [Fact]
    public async Task ExecuteAsync_DeletesFileCreatedByReplica()
    {
        string destinationRoot = CreateDirectory("created-destination");
        string destination = Path.Combine(destinationRoot, "created.txt");
        FileRestoreRequest request = FileRequest(destinationRoot, destination, "created-content");
        RollbackJournalService service = CreateService();
        RestoreAction action = FileAction("file-created");
        await PrepareAsync(service, "created-session", action, request);
        File.WriteAllText(destination, "created-content", Encoding.UTF8);
        await MarkVerifiedAsync(service, "created-session", action.Id, destination);

        await ExecuteAllAsync(service, "created-session");

        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task ExecuteAsync_RestoresEnvironmentVariable()
    {
        _environment.Set("REPLICA_TEST_HOME", "before", EnvironmentVariableScope.User);
        RollbackJournalService service = CreateService();
        RestoreAction action = EnvironmentAction("environment", "User:REPLICA_TEST_HOME", "after", false);
        await PrepareAsync(service, "environment-session", action);
        _environment.Set("REPLICA_TEST_HOME", "after", EnvironmentVariableScope.User);
        await MarkVerifiedAsync(service, "environment-session", action.Id);

        await ExecuteAllAsync(service, "environment-session");

        Assert.Equal("before", _environment.Get("REPLICA_TEST_HOME", EnvironmentVariableScope.User));
        Assert.Equal(1, _notifier.NotifyCount);
    }

    [Fact]
    public async Task ExecuteAsync_RestoresWholeOriginalPathDuringRollback()
    {
        _environment.Set("PATH", "C:\\One;C:\\Two", EnvironmentVariableScope.User);
        RollbackJournalService service = CreateService();
        RestoreAction action = EnvironmentAction("path", "PATH:user:C:\\Three:0000", "C:\\Three", true);
        await PrepareAsync(service, "path-session", action);
        _environment.Set("PATH", "C:\\One;C:\\Two;C:\\Three", EnvironmentVariableScope.User);
        await MarkVerifiedAsync(service, "path-session", action.Id);

        await ExecuteAllAsync(service, "path-session");

        Assert.Equal("C:\\One;C:\\Two", _environment.Get("PATH", EnvironmentVariableScope.User));
    }

    [Fact]
    public async Task ExecuteAsync_RestoresRegistryValue()
    {
        const string before = "light";
        _registry.Value = before;
        RollbackJournalService service = CreateService();
        RestoreAction action = RegistryAction("registry");
        await PrepareAsync(service, "registry-session", action);
        _registry.Value = "dark";
        await MarkVerifiedAsync(service, "registry-session", action.Id);

        await ExecuteAllAsync(service, "registry-session");

        Assert.Equal(before, _registry.Value);
    }

    [Fact]
    public async Task ExecuteAsync_ContinuesIndependentItemsAfterIntermediateFailure()
    {
        _environment.Set("FIRST_SETTING", "first-before", EnvironmentVariableScope.User);
        _environment.Set("SECOND_SETTING", "second-before", EnvironmentVariableScope.User);
        RollbackJournalService service = CreateService();
        RestoreAction first = EnvironmentAction("first", "FIRST_SETTING", "first-after", false);
        RestoreAction second = EnvironmentAction("second", "SECOND_SETTING", "second-after", false);
        await PrepareAsync(service, "partial-failure", first);
        _environment.Set("FIRST_SETTING", "first-after", EnvironmentVariableScope.User);
        await MarkVerifiedAsync(service, "partial-failure", first.Id);
        await PrepareAsync(service, "partial-failure", second);
        _environment.Set("SECOND_SETTING", "second-after", EnvironmentVariableScope.User);
        await MarkVerifiedAsync(service, "partial-failure", second.Id);
        _environment.Set("FIRST_SETTING", "user-changed", EnvironmentVariableScope.User);

        RollbackExecutionResult result = await ExecuteAllAsync(service, "partial-failure");

        Assert.Equal(RollbackJournalState.RollbackFailed, result.State);
        Assert.True(result.WasPartial);
        Assert.Equal("user-changed", _environment.Get("FIRST_SETTING", EnvironmentVariableScope.User));
        Assert.Equal("second-before", _environment.Get("SECOND_SETTING", EnvironmentVariableScope.User));
        Assert.Contains(result.Items, item => item.State == RollbackJournalState.RollbackFailed);
        Assert.Contains(result.Items, item => item.State == RollbackJournalState.RolledBack);
    }

    [Fact]
    public async Task CreatePlanAsync_RejectsCorruptJournalJson()
    {
        RollbackJournalService service = CreateService();
        RestoreAction action = EnvironmentAction("corrupt", "CORRUPT_SETTING", "after", false);
        await PrepareAsync(service, "corrupt-session", action);
        string checksums = Path.Combine(SessionDirectory("corrupt-session"), "checksums.json");
        File.WriteAllText(checksums, "{not-json", Encoding.UTF8);

        await Assert.ThrowsAnyAsync<Exception>(() => service.CreatePlanAsync(
            "corrupt-session",
            null,
            CancellationToken.None));
        Assert.Null(_environment.Get("CORRUPT_SETTING", EnvironmentVariableScope.User));
    }

    [Fact]
    public async Task CreatePlanAsync_RejectsChecksumMismatch()
    {
        RollbackJournalService service = CreateService();
        RestoreAction action = EnvironmentAction("checksum", "CHECKSUM_SETTING", "after", false);
        await PrepareAsync(service, "checksum-session", action);
        string payload = Assert.Single(Directory.EnumerateFiles(
            Path.Combine(SessionDirectory("checksum-session"), "environment"),
            "*.json"));
        await File.AppendAllTextAsync(payload, " ");

        await Assert.ThrowsAsync<InvalidDataException>(() => service.CreatePlanAsync(
            "checksum-session",
            null,
            CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_TreatsCompletedDuplicateRollbackAsNoOp()
    {
        _environment.Set("DUPLICATE_SETTING", "before", EnvironmentVariableScope.User);
        RollbackJournalService service = CreateService();
        RestoreAction action = EnvironmentAction("duplicate", "DUPLICATE_SETTING", "after", false);
        await PrepareAsync(service, "duplicate-session", action);
        _environment.Set("DUPLICATE_SETTING", "after", EnvironmentVariableScope.User);
        await MarkVerifiedAsync(service, "duplicate-session", action.Id);
        RollbackPlan plan = (await service.CreatePlanAsync(
            "duplicate-session",
            null,
            CancellationToken.None)).Approve();
        await service.ExecuteAsync(plan, null, CancellationToken.None);

        RollbackExecutionResult duplicate = await service.ExecuteAsync(
            plan,
            null,
            CancellationToken.None);

        Assert.True(duplicate.WasAlreadyRolledBack);
        Assert.Empty(duplicate.Items);
        Assert.Equal("before", _environment.Get("DUPLICATE_SETTING", EnvironmentVariableScope.User));
    }

    [Fact]
    public async Task ExecuteAsync_RollsBackOnlySelectedItems()
    {
        _environment.Set("SELECTED_SETTING", "selected-before", EnvironmentVariableScope.User);
        _environment.Set("REMAINING_SETTING", "remaining-before", EnvironmentVariableScope.User);
        RollbackJournalService service = CreateService();
        RestoreAction selected = EnvironmentAction("selected", "SELECTED_SETTING", "selected-after", false);
        RestoreAction remaining = EnvironmentAction("remaining", "REMAINING_SETTING", "remaining-after", false);
        await PrepareAsync(service, "selection-session", selected);
        _environment.Set("SELECTED_SETTING", "selected-after", EnvironmentVariableScope.User);
        await MarkVerifiedAsync(service, "selection-session", selected.Id);
        await PrepareAsync(service, "selection-session", remaining);
        _environment.Set("REMAINING_SETTING", "remaining-after", EnvironmentVariableScope.User);
        await MarkVerifiedAsync(service, "selection-session", remaining.Id);
        RollbackPlan plan = (await service.CreatePlanAsync(
            "selection-session",
            [selected.Id],
            CancellationToken.None)).Approve();

        RollbackExecutionResult result = await service.ExecuteAsync(plan, null, CancellationToken.None);

        Assert.Equal(RollbackJournalState.RollbackPending, result.State);
        Assert.True(result.WasPartial);
        Assert.Equal("selected-before", _environment.Get("SELECTED_SETTING", EnvironmentVariableScope.User));
        Assert.Equal("remaining-after", _environment.Get("REMAINING_SETTING", EnvironmentVariableScope.User));
    }

    [Fact]
    public async Task CreatePlanAsync_NeverSelectsApplicationForAutomaticRemoval()
    {
        RollbackJournalService service = CreateService();
        RestoreAction package = PackageAction("package");
        await PrepareAsync(service, "package-session", package);
        await MarkVerifiedAsync(service, "package-session", package.Id);

        RollbackPlan plan = await service.CreatePlanAsync(
            "package-session",
            null,
            CancellationToken.None);
        RollbackPreviewItem item = Assert.Single(plan.Items);

        Assert.Equal(RollbackItemKind.ApplicationInstallation, item.Kind);
        Assert.False(item.CanRollbackAutomatically);
        Assert.False(item.IsSelected);
        Assert.Contains("never removes", item.ManualInstruction, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecordBeforeMutationAsync_NeverCapturesSensitiveEnvironmentValue()
    {
        _environment.Set("SERVICE_API_TOKEN", "do-not-store", EnvironmentVariableScope.User);
        RollbackJournalService service = CreateService();
        RestoreAction action = EnvironmentAction(
            "sensitive",
            "SERVICE_API_TOKEN",
            "replacement",
            false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => PrepareAsync(
            service,
            "sensitive-session",
            action));

        Assert.False(File.Exists(Path.Combine(
            SessionDirectory("sensitive-session"),
            "manifest.json")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private RollbackJournalService CreateService()
    {
        return new RollbackJournalService(
            new ReplicaPathProvider(_root),
            _environment,
            _notifier,
            _registry,
            new BuiltInRegistryWriteAllowList(),
            TimeProvider.System);
    }

    private static async Task PrepareAsync(
        RollbackJournalService service,
        string sessionId,
        RestoreAction action,
        FileRestoreRequest? request = null)
    {
        await service.RecordBeforeMutationAsync(
            new RestoreJournalEntry(
                sessionId,
                action.Id,
                action.Type,
                DateTimeOffset.UtcNow,
                null,
                "BeforeMutation",
                action,
                request),
            CancellationToken.None);
    }

    private static Task MarkVerifiedAsync(
        RollbackJournalService service,
        string sessionId,
        string actionId,
        string? mutationTargetPath = null)
    {
        return service.MarkActionStateAsync(
            sessionId,
            actionId,
            RollbackJournalState.Verified,
            mutationTargetPath,
            CancellationToken.None);
    }

    private static async Task<RollbackExecutionResult> ExecuteAllAsync(
        RollbackJournalService service,
        string sessionId)
    {
        RollbackPlan plan = (await service.CreatePlanAsync(
            sessionId,
            null,
            CancellationToken.None)).Approve();
        return await service.ExecuteAsync(plan, null, CancellationToken.None);
    }

    private string CreateDirectory(string relative)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string WriteFile(string root, string relative, string value)
    {
        string path = Path.Combine(root, relative);
        File.WriteAllText(path, value, Encoding.UTF8);
        return path;
    }

    private FileRestoreRequest FileRequest(string destinationRoot, string destination, string targetValue)
    {
        string snapshotRoot = CreateDirectory($"snapshot-{Guid.NewGuid():N}");
        string source = WriteFile(snapshotRoot, "source.bin", targetValue);
        return new FileRestoreRequest(
            snapshotRoot,
            "source.bin",
            destination,
            [destinationRoot],
            HashFile(source),
            1024 * 1024,
            true,
            RestoreFileConflictBehavior.OverwriteWithSnapshot,
            Path.Combine(_root, "legacy-backup"));
    }

    private void AssertJournalShape(string sessionId)
    {
        string session = SessionDirectory(sessionId);
        Assert.True(File.Exists(Path.Combine(session, "manifest.json")));
        Assert.True(Directory.Exists(Path.Combine(session, "files")));
        Assert.True(Directory.Exists(Path.Combine(session, "registry")));
        Assert.True(Directory.Exists(Path.Combine(session, "environment")));
        Assert.True(File.Exists(Path.Combine(session, "checksums.json")));
    }

    private string SessionDirectory(string sessionId)
    {
        return Path.Combine(_root, "Replica", "Rollback", sessionId);
    }

    private static string HashFile(string path)
    {
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    }

    private static RestoreAction EnvironmentAction(
        string id,
        string key,
        string target,
        bool path)
    {
        return Action(
            id,
            path ? RestoreActionType.AddPathEntry : RestoreActionType.SetUserEnvironmentVariable,
            path ? DiffArea.Path : DiffArea.EnvironmentVariables,
            key,
            target);
    }

    private static RestoreAction RegistryAction(string id)
    {
        return Action(
            id,
            RestoreActionType.RestoreRegistryValue,
            DiffArea.PluginSettings,
            "registry:HKCU\\Software\\Replica\\RestorableSettings|Theme|String",
            "dark");
    }

    private static RestoreAction FileAction(string id)
    {
        return Action(id, RestoreActionType.RestoreSelectedUserFile, DiffArea.SelectedUserFiles, id, "target");
    }

    private static RestoreAction PackageAction(string id)
    {
        return Action(id, RestoreActionType.InstallPackage, DiffArea.Applications, "Git.Git", "2.51.0") with
        {
            MatchConfidence = ApplicationMatchConfidence.Exact,
        };
    }

    private static RestoreAction Action(
        string id,
        RestoreActionType type,
        DiffArea area,
        string key,
        string target)
    {
        return new RestoreAction(
            id,
            type,
            id,
            "test",
            null,
            null,
            target,
            DiffRiskLevel.Low,
            false,
            false,
            true,
            [],
            TimeSpan.Zero,
            0,
            true,
            false,
            area,
            key,
            "Test");
    }

    private sealed class FakeEnvironmentStore : IEnvironmentVariableStore
    {
        private readonly Dictionary<(string Name, EnvironmentVariableScope Scope), string> _values = new();

        public string? Get(string name, EnvironmentVariableScope scope)
        {
            return _values.GetValueOrDefault((name.ToUpperInvariant(), scope));
        }

        public void Set(string name, string? value, EnvironmentVariableScope scope)
        {
            (string, EnvironmentVariableScope) key = (name.ToUpperInvariant(), scope);
            if (value is null)
            {
                _values.Remove(key);
            }
            else
            {
                _values[key] = value;
            }
        }
    }

    private sealed class FakeEnvironmentNotifier : IEnvironmentChangeNotifier
    {
        public int NotifyCount { get; private set; }

        public void NotifyEnvironmentChanged() => NotifyCount++;
    }

    private sealed class FakeRegistryStore : IRegistryValueStore
    {
        public object? Value { get; set; }

        public object? GetValue(RegistryWriteRequest request) => Value;

        public void SetValue(RegistryWriteRequest request, object value) => Value = value;

        public void DeleteValue(RegistryWriteRequest request) => Value = null;
    }
}
