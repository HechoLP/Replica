using Replica.App.Services;
using Replica.App.ViewModels;
using Replica.Core.Diffing;
using Replica.Core.Planning;
using Replica.Core.Recovery;
using Replica.Core.Services;
using Replica.Core.Snapshots;

namespace Replica.IntegrationTests;

public sealed class RecoveryWizardViewModelTests
{
    [Fact]
    public async Task BeginCommand_OpensRecoverySnapshotAndDisplaysAllNineteenSteps()
    {
        FakeRecoveryWizardService wizard = new();
        FakeRecoveryDialogs dialogs = new() { SnapshotPath = "recovery.replica" };
        RecoveryWizardViewModel viewModel = new(wizard, dialogs);

        await viewModel.BeginAsync();

        Assert.True(viewModel.IsVisible);
        Assert.Equal(19, viewModel.Steps.Count);
        Assert.Contains("OLD-PC", viewModel.SourceComputerText, StringComparison.Ordinal);
        Assert.Contains("4 / 19", viewModel.CurrentStepText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EncryptedSnapshot_PromptsAndClearsPasswordBuffer()
    {
        FakeRecoveryWizardService wizard = new() { RequirePasswordOnFirstOpen = true };
        FakeRecoveryDialogs dialogs = new()
        {
            SnapshotPath = "encrypted.replica",
            PasswordToReturn = "top-secret".ToCharArray(),
        };
        RecoveryWizardViewModel viewModel = new(wizard, dialogs);

        await viewModel.BeginAsync();

        Assert.Equal(2, wizard.StartCalls);
        Assert.All(dialogs.ReturnedPassword!, value => Assert.Equal('\0', value));
        Assert.DoesNotContain("top-secret", viewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CorruptSnapshot_ShowsGenericSafeMessage()
    {
        FakeRecoveryWizardService wizard = new() { ThrowCorruptSnapshot = true };
        FakeRecoveryDialogs dialogs = new() { SnapshotPath = "corrupt.replica" };
        RecoveryWizardViewModel viewModel = new(wizard, dialogs);

        await viewModel.BeginAsync();

        Assert.Contains("손상", viewModel.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("internal-sensitive-detail", dialogs.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnalyzeDisplaysEveryTypedRestoreActionBeforeApproval()
    {
        FakeRecoveryWizardService wizard = new();
        RecoveryWizardViewModel viewModel = new(
            wizard,
            new FakeRecoveryDialogs { SnapshotPath = "recovery.replica" });
        await viewModel.BeginAsync();

        await viewModel.AnalyzeCommand.ExecuteAsync(null);

        RestoreAction action = Assert.Single(viewModel.PlanActions);
        Assert.Equal(RestoreActionType.InstallPackage, action.Type);
        Assert.Equal("snapshot", action.OriginalValue);
        Assert.Equal("current", action.CurrentValue);
        Assert.Equal("target", action.TargetValue);
        Assert.True(viewModel.CanApprovePlan);
    }

    private sealed class FakeRecoveryDialogs : IRecoveryDialogService
    {
        public string? SnapshotPath { get; init; }

        public char[]? PasswordToReturn { get; init; }

        public char[]? ReturnedPassword { get; private set; }

        public string LastError { get; private set; } = string.Empty;

        public string? SelectRecoverySnapshot() => SnapshotPath;

        public char[]? RequestPassword(string title, string message)
        {
            ReturnedPassword = PasswordToReturn;
            return PasswordToReturn;
        }

        public bool Confirm(string title, string message) => true;

        public void ShowError(string title, string message) => LastError = message;
    }

    private sealed class FakeRecoveryWizardService : IRecoveryWizardService
    {
        public bool RequirePasswordOnFirstOpen { get; init; }

        public bool ThrowCorruptSnapshot { get; init; }

        public int StartCalls { get; private set; }

        public Task<RecoveryStartResult> StartAsync(
            string snapshotPath,
            ReadOnlyMemory<char> password,
            CancellationToken cancellationToken)
        {
            StartCalls++;
            if (ThrowCorruptSnapshot)
            {
                throw new ReplicaSnapshotException("internal-sensitive-detail");
            }

            if (RequirePasswordOnFirstOpen && password.IsEmpty)
            {
                return Task.FromResult(new RecoveryStartResult(null, true));
            }

            return Task.FromResult(new RecoveryStartResult(CreateSession(
                encrypted: RequirePasswordOnFirstOpen), false));
        }

        public Task<RecoveryWizardSession> AnalyzeAsync(
            string sessionId,
            IReadOnlyList<RecoveryPathMapping> mappings,
            ReadOnlyMemory<char> password,
            CancellationToken cancellationToken) => Task.FromResult(CreateSession(
                plan: CreatePlan(),
                status: RecoveryWizardStatus.AwaitingApproval,
                step: RecoveryWizardStep.FinalApproval));

        public Task<RecoveryWizardSession> ApprovePlanAsync(
            string sessionId,
            CancellationToken cancellationToken) => Task.FromResult(CreateSession());

        public Task<RecoveryWizardSession> ExecuteAsync(
            string sessionId,
            ReadOnlyMemory<char> password,
            CancellationToken cancellationToken) => Task.FromResult(CreateSession());

        public Task<RecoveryWizardSession> RetryFailedAsync(
            string sessionId,
            ReadOnlyMemory<char> password,
            CancellationToken cancellationToken) => Task.FromResult(CreateSession());

        public Task<RecoveryWizardSession> ApproveRestartAsync(
            string sessionId,
            bool userApproved,
            CancellationToken cancellationToken) => Task.FromResult(CreateSession());

        public Task<RecoveryWizardSession?> LoadForResumeAsync(
            string sessionId,
            CancellationToken cancellationToken) => Task.FromResult<RecoveryWizardSession?>(CreateSession());

        public Task<RecoveryWizardSession> ConfirmResumeAsync(
            string sessionId,
            ReadOnlyMemory<char> password,
            CancellationToken cancellationToken) => Task.FromResult(CreateSession());

        public Task<RecoveryWizardSession> CancelAsync(
            string sessionId,
            CancellationToken cancellationToken) => Task.FromResult(CreateSession());

        private static RecoveryWizardSession CreateSession(
            bool encrypted = false,
            RestorePlan? plan = null,
            RecoveryWizardStatus status = RecoveryWizardStatus.InProgress,
            RecoveryWizardStep step = RecoveryWizardStep.ShowSourceComputer)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return new RecoveryWizardSession(
                Guid.NewGuid().ToString("N"),
                "recovery.replica",
                Guid.NewGuid(),
                status,
                step,
                "OLD-PC",
                "11 24H2",
                "x64",
                "ko-KR",
                encrypted,
                plan,
                [],
                [],
                [],
                [],
                [],
                [],
                null,
                null,
                false,
                false,
                now,
                now);
        }

        private static RestorePlan CreatePlan()
        {
            RestoreAction action = new(
                "install",
                RestoreActionType.InstallPackage,
                "Install editor",
                "Install the approved package.",
                "snapshot",
                "current",
                "target",
                DiffRiskLevel.Low,
                false,
                false,
                false,
                [],
                TimeSpan.FromMinutes(1),
                0,
                true,
                false,
                DiffArea.Applications,
                "Example.Editor",
                "Missing");
            return new RestorePlan(
                "plan",
                DiffRestoreMode.Recommended,
                [action],
                new RestoreDryRunSummary(1, 0, 0, 0, 0, 0, false, 0, 0, 1, TimeSpan.FromMinutes(1), 0),
                RestorePlanReviewStatus.PendingReview,
                DateTimeOffset.UtcNow);
        }
    }
}
