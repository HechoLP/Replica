using Replica.App.Services;
using Replica.App.ViewModels;
using Replica.Core.Diffing;
using Replica.Core.History;
using Replica.Core.Planning;
using Replica.Core.Recovery;
using Replica.Core.Services;
using Replica.Core.Snapshots;

namespace Replica.IntegrationTests;

public sealed class SnapshotHistoryViewModelTests
{
    [Fact]
    public async Task ShowAndCompare_DisplaysHistoryAndTypedChanges()
    {
        FakeHistoryService service = new();
        SnapshotHistoryViewModel viewModel = CreateViewModel(service, out _, out _, out _);
        await viewModel.ShowAsync();

        await viewModel.CompareCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsVisible);
        Assert.Equal(2, viewModel.Snapshots.Count);
        Assert.Contains("추가 1", viewModel.ComparisonSummary, StringComparison.Ordinal);
        Assert.Equal("Cursor", Assert.Single(viewModel.ComparisonItems).Name);
    }

    [Fact]
    public async Task DeleteCommand_RequiresDialogChoiceAndPassesExplicitConfirmation()
    {
        FakeHistoryService service = new();
        SnapshotHistoryViewModel viewModel = CreateViewModel(
            service,
            out _,
            out FakeDeleteDialogs deleteDialogs,
            out _);
        await viewModel.ShowAsync();
        deleteDialogs.Choice = SnapshotDeleteChoice.RemoveHistoryOnly;

        await viewModel.DeleteSnapshotCommand.ExecuteAsync(null);

        Assert.True(service.DeleteConfirmed);
        Assert.False(service.DeleteFile);
    }

    [Fact]
    public async Task PastStateCommand_DisplaysUnapprovedDryRunPlan()
    {
        FakeHistoryService service = new();
        SnapshotHistoryViewModel viewModel = CreateViewModel(
            service,
            out _,
            out _,
            out RestoreDryRunViewModel dryRun);
        await viewModel.ShowAsync();

        await viewModel.CreatePastRestorePlanCommand.ExecuteAsync(null);

        Assert.True(dryRun.CanReview);
        Assert.Null(dryRun.ReviewedPlan);
        Assert.Contains("자동 실행하지 않습니다", viewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddEncryptedSnapshot_ClearsPasswordAfterIndexing()
    {
        FakeHistoryService service = new() { RequirePassword = true };
        SnapshotHistoryViewModel viewModel = CreateViewModel(
            service,
            out FakeRecoveryDialogs dialogs,
            out _,
            out _);

        await viewModel.AddSnapshotCommand.ExecuteAsync(null);

        Assert.Equal(2, service.AddCalls);
        Assert.All(dialogs.Password, character => Assert.Equal('\0', character));
    }

    [Fact]
    public async Task OpenFromCommandLine_IndexesAndDisplaysSnapshot()
    {
        FakeHistoryService service = new();
        SnapshotHistoryViewModel viewModel = CreateViewModel(service, out _, out _, out _);

        await viewModel.OpenFromCommandLineAsync("C:\\Snapshots\\recovery.replica");

        Assert.True(viewModel.IsVisible);
        Assert.Equal(1, service.AddCalls);
        Assert.Equal(2, viewModel.Snapshots.Count);
    }

    private static SnapshotHistoryViewModel CreateViewModel(
        FakeHistoryService service,
        out FakeRecoveryDialogs recoveryDialogs,
        out FakeDeleteDialogs deleteDialogs,
        out RestoreDryRunViewModel dryRun)
    {
        recoveryDialogs = new FakeRecoveryDialogs();
        deleteDialogs = new FakeDeleteDialogs();
        dryRun = new RestoreDryRunViewModel();
        return new SnapshotHistoryViewModel(service, recoveryDialogs, deleteDialogs, dryRun);
    }

    private sealed class FakeRecoveryDialogs : IRecoveryDialogService
    {
        public char[] Password { get; } = "secret".ToCharArray();

        public string? SelectRecoverySnapshot() => "history.replica";

        public string? SelectOfflineInstallerExportFolder() => null;

        public char[]? RequestPassword(string title, string message) => Password;

        public bool Confirm(string title, string message) => true;

        public void ShowError(string title, string message)
        {
        }
    }

    private sealed class FakeDeleteDialogs : ISnapshotHistoryDialogService
    {
        public SnapshotDeleteChoice Choice { get; set; } = SnapshotDeleteChoice.Cancel;

        public SnapshotDeleteChoice ConfirmDelete(string snapshotName, bool fileExists) => Choice;
    }

    private sealed class FakeHistoryService : ISnapshotHistoryService
    {
        private readonly SnapshotHistoryEntry[] _entries =
        [
            Entry(Guid.Parse("11111111-1111-1111-1111-111111111111"), "2026-08-04", true),
            Entry(Guid.Parse("22222222-2222-2222-2222-222222222222"), "2026-09-01", true),
        ];

        public bool RequirePassword { get; init; }

        public int AddCalls { get; private set; }

        public bool DeleteConfirmed { get; private set; }

        public bool DeleteFile { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<SnapshotHistoryEntry> AddSnapshotAsync(
            string snapshotPath,
            ReadOnlyMemory<char> password,
            int? environmentScore,
            CancellationToken cancellationToken)
        {
            AddCalls++;
            if (RequirePassword && password.IsEmpty)
            {
                throw new ReplicaSnapshotDecryptionException();
            }

            return Task.FromResult(_entries[0]);
        }

        public Task<IReadOnlyList<SnapshotHistoryEntry>> GetSnapshotsAsync(
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SnapshotHistoryEntry>>(_entries);

        public Task UpdateSnapshotAsync(
            Guid snapshotId,
            SnapshotHistoryUpdate update,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteSnapshotAsync(
            Guid snapshotId,
            bool deleteSnapshotFile,
            bool userConfirmed,
            CancellationToken cancellationToken)
        {
            DeleteConfirmed = userConfirmed;
            DeleteFile = deleteSnapshotFile;
            return Task.CompletedTask;
        }

        public Task<SnapshotComparisonResult> CompareAsync(
            Guid fromSnapshotId,
            Guid toSnapshotId,
            CancellationToken cancellationToken) => Task.FromResult(new SnapshotComparisonResult(
                _entries[0],
                _entries[1],
                [
                    new SnapshotComparisonItem(
                        SnapshotComparisonChangeKind.Added,
                        SnapshotComparisonArea.Application,
                        "winget:ANYSHPERE.CURSOR",
                        "Cursor",
                        null,
                        "1.0",
                        null,
                        null,
                        null,
                        null,
                        null,
                        null),
                ]));

        public Task<PastStateRestorePlan> CreatePastStateRestorePlanAsync(
            Guid snapshotId,
            IReadOnlyList<RecoveryPathMapping> mappings,
            ReadOnlyMemory<char> password,
            CancellationToken cancellationToken)
        {
            RestorePlan plan = new(
                "past-plan",
                DiffRestoreMode.Safe,
                [],
                new RestoreDryRunSummary(0, 0, 0, 0, 0, 0, false, 0, 0, 0, TimeSpan.Zero, 0),
                RestorePlanReviewStatus.PendingReview,
                DateTimeOffset.UtcNow);
            return Task.FromResult(new PastStateRestorePlan(snapshotId, plan, 90));
        }

        public Task RecordRestoreAsync(
            RestoreHistoryRecord record,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RecordRollbackAsync(
            RollbackHistoryRecord record,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SavePackageMatchingOverrideAsync(
            PackageMatchingOverride matchingOverride,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<PackageMatchingOverride>> GetPackageMatchingOverridesAsync(
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PackageMatchingOverride>>([]);

        private static SnapshotHistoryEntry Entry(Guid id, string name, bool exists) => new(
            id,
            name,
            string.Empty,
            [],
            $"C:\\Snapshots\\{name}.replica",
            new string('A', 64),
            DateTimeOffset.UtcNow,
            SnapshotType.Lightweight,
            "PC",
            1024,
            false,
            90,
            exists);
    }
}
