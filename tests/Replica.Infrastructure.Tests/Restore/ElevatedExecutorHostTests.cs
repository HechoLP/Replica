using Replica.Core.Diffing;
using Replica.Core.Execution;
using Replica.Core.Matching;
using Replica.Core.Planning;
using Replica.Core.Rollback;
using Replica.Core.Services;
using Replica.Infrastructure.Restore;

namespace Replica.Infrastructure.Tests.Restore;

public sealed class ElevatedExecutorHostTests
{
    [Fact]
    public async Task RunAsync_RequiresProtectedConsentBeforeAnyMutation()
    {
        RestoreAction action = Action();
        FakeExecutor executor = new();
        CapturingConsent consent = new(userApproved: false);
        ElevatedExecutorHost host = new(
            new FakePlanStore(Envelope(action)),
            executor,
            new NullJournal(),
            new NullProgress(),
            consent,
            isAdministrator: () => true);

        int exitCode = await host.RunAsync(
            new ElevatedExecutorArguments("plan.json", new string('A', 64), new string('B', 64), "session"),
            CancellationToken.None);

        Assert.Equal(1223, exitCode);
        Assert.Equal(action, consent.Action);
        Assert.Equal(0, executor.ExecuteCount);
    }

    private static ElevatedRestorePlanEnvelope Envelope(RestoreAction action) => new(
        ElevatedRestorePlanEnvelope.CurrentSchemaVersion,
        "session",
        DateTimeOffset.UtcNow.AddMinutes(1),
        new string('C', 64),
        new RestorePlan(
            "plan",
            DiffRestoreMode.Safe,
            [action],
            new RestoreDryRunSummary(0, 0, 1, 0, 1, 1, false, 1, 0, 1, TimeSpan.Zero, 0),
            RestorePlanReviewStatus.Approved,
            DateTimeOffset.UtcNow),
        []);

    private static RestoreAction Action() => new(
        "machine-environment",
        RestoreActionType.SetMachineEnvironmentVariable,
        "Machine environment",
        "Set an approved machine environment variable.",
        null,
        null,
        "C:\\Tools",
        DiffRiskLevel.Medium,
        true,
        false,
        true,
        [],
        TimeSpan.Zero,
        0,
        true,
        false,
        DiffArea.EnvironmentVariables,
        "Machine:REPLICA_TOOLS",
        "Fixture",
        ApplicationMatchConfidence.Exact);

    private sealed class FakePlanStore(ElevatedRestorePlanEnvelope envelope) : IElevatedPlanStore
    {
        public Task<ElevatedRestorePlanEnvelope> ConsumeAsync(
            ElevatedExecutorArguments arguments,
            CancellationToken cancellationToken) => Task.FromResult(envelope);

        public Task<ElevatedPlanCreationResult> CreateAsync(
            RestorePlan approvedPlan,
            RestoreAction administratorAction,
            IRestoreExecutionContext context,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DiscardAsync(
            ElevatedPlanLaunchRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class CapturingConsent(bool userApproved) : IElevatedActionConsentService
    {
        public RestoreAction? Action { get; private set; }

        public Task<bool> ConfirmAsync(RestoreAction action, CancellationToken cancellationToken)
        {
            Action = action;
            return Task.FromResult(userApproved);
        }
    }

    private sealed class FakeExecutor : IRestoreExecutor
    {
        public int ExecuteCount { get; private set; }

        public Task<RestoreExecutionResult> ExecuteAsync(
            RestorePlan plan,
            IRestoreExecutionContext context,
            IRestoreProgressReporter progressReporter,
            CancellationToken cancellationToken)
        {
            ExecuteCount++;
            return Task.FromResult(new RestoreExecutionResult(context.SessionId, [], false, false));
        }
    }

    private sealed class NullJournal : IRestoreJournal
    {
        public Task RecordBeforeMutationAsync(
            RestoreJournalEntry entry,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task MarkActionStateAsync(
            string sessionId,
            string actionId,
            RollbackJournalState state,
            string? mutationTargetPath,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NullProgress : IRestoreProgressReporter
    {
        public Task ReportAsync(RestoreProgress progress, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
