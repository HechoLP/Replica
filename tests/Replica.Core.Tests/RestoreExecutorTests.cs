using Replica.Core.Diffing;
using Replica.Core.Execution;
using Replica.Core.Planning;
using Replica.Core.Rollback;
using Replica.Core.Services;

namespace Replica.Core.Tests;

public sealed class RestoreExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_SkipsDependentActionAfterFailure()
    {
        RestoreAction first = Action("first", []);
        RestoreAction second = Action("second", [first.Id]);
        RestorePlan plan = Plan(first, second);
        FakeHandler handler = new(first.Id);
        FakeJournal journal = new();
        RestoreExecutor executor = new([handler], new FakeElevationService());

        RestoreExecutionResult result = await executor.ExecuteAsync(
            plan,
            new FakeContext(journal),
            new FakeProgressReporter(),
            CancellationToken.None);

        Assert.Equal(RestoreExecutionState.Failed, result.Actions[0].State);
        Assert.Equal(RestoreExecutionState.Skipped, result.Actions[1].State);
        Assert.Equal("DependencyNotSucceeded", result.Actions[1].ReasonCode);
        Assert.Equal([first.Id], journal.ActionIds);
        Assert.Equal([first.Id], handler.ExecutedActionIds);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotMutateWhenJournalFails()
    {
        RestoreAction action = Action("journal-failure", []);
        FakeHandler handler = new();
        RestoreExecutor executor = new([handler], new FakeElevationService());

        RestoreExecutionResult result = await executor.ExecuteAsync(
            Plan(action),
            new FakeContext(new FakeJournal(shouldFail: true)),
            new FakeProgressReporter(),
            CancellationToken.None);

        Assert.Equal(RestoreExecutionState.Failed, Assert.Single(result.Actions).State);
        Assert.Empty(handler.ExecutedActionIds);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsPlanWithoutExplicitApproval()
    {
        RestoreAction action = Action("pending", []);
        RestorePlan pending = Plan(action) with
        {
            ReviewStatus = RestorePlanReviewStatus.PendingReview,
        };
        RestoreExecutor executor = new([new FakeHandler()], new FakeElevationService());

        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(
            pending,
            new FakeContext(new FakeJournal()),
            new FakeProgressReporter(),
            CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_RunsAdministratorActionDirectlyInsideElevatedContext()
    {
        RestoreAction action = Action("administrator", []) with
        {
            RequiresAdministrator = true,
        };
        FakeHandler handler = new();
        FakeJournal journal = new();
        RestoreExecutor executor = new([handler], new FakeElevationService());

        RestoreExecutionResult result = await executor.ExecuteAsync(
            Plan(action),
            new FakeContext(journal, isElevated: true),
            new FakeProgressReporter(),
            CancellationToken.None);

        Assert.Equal(RestoreExecutionState.Succeeded, Assert.Single(result.Actions).State);
        Assert.Equal([action.Id], journal.ActionIds);
        Assert.Equal([action.Id], handler.ExecutedActionIds);
    }

    [Fact]
    public void RestoreExecutionStates_MatchExecutionContract()
    {
        Assert.Equal(
            [
                "Pending",
                "Running",
                "Succeeded",
                "Failed",
                "Skipped",
                "Cancelled",
                "RequiresRestart",
                "RolledBack",
            ],
            Enum.GetNames<RestoreExecutionState>());
    }

    private static RestoreAction Action(string id, IReadOnlyList<string> dependencies)
    {
        return new RestoreAction(
            id,
            RestoreActionType.RestoreFile,
            id,
            "test",
            null,
            null,
            "target",
            DiffRiskLevel.Low,
            false,
            false,
            true,
            dependencies,
            TimeSpan.Zero,
            0,
            true,
            false,
            DiffArea.ConfigurationFiles,
            id,
            "Test");
    }

    private static RestorePlan Plan(params RestoreAction[] actions)
    {
        return new RestorePlan(
            "plan",
            DiffRestoreMode.Safe,
            actions,
            new RestoreDryRunSummary(0, 0, 0, 0, 0, 0, false, 0, 0, actions.Length, TimeSpan.Zero, 0),
            RestorePlanReviewStatus.Approved,
            DateTimeOffset.UtcNow);
    }

    private sealed class FakeHandler(params string[] failingActionIds) : IRestoreActionHandler
    {
        private readonly HashSet<string> _failing = failingActionIds.ToHashSet(StringComparer.Ordinal);

        public List<string> ExecutedActionIds { get; } = [];

        public bool CanHandle(RestoreActionType actionType) => true;

        public Task<RestoreActionExecutionResult> ExecuteAsync(
            RestoreAction action,
            IRestoreExecutionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecutedActionIds.Add(action.Id);
            bool failed = _failing.Contains(action.Id);
            return Task.FromResult(new RestoreActionExecutionResult(
                action.Id,
                failed ? RestoreExecutionState.Failed : RestoreExecutionState.Succeeded,
                failed ? "TestFailure" : "TestSuccess",
                "test"));
        }
    }

    private sealed class FakeJournal(bool shouldFail = false) : IRestoreJournal
    {
        public List<string> ActionIds { get; } = [];

        public Task RecordBeforeMutationAsync(
            RestoreJournalEntry entry,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (shouldFail)
            {
                throw new IOException("Test journal failure.");
            }

            ActionIds.Add(entry.ActionId);
            return Task.CompletedTask;
        }

        public Task MarkActionStateAsync(
            string sessionId,
            string actionId,
            RollbackJournalState state,
            string? mutationTargetPath,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeContext(IRestoreJournal journal, bool isElevated = false) : IRestoreExecutionContext
    {
        public string SessionId => "test-session";

        public bool IsElevated => isElevated;

        public IRestoreJournal Journal => journal;

        public FileRestoreRequest? GetFileRestoreRequest(string actionId) => null;
    }

    private sealed class FakeProgressReporter : IRestoreProgressReporter
    {
        public Task ReportAsync(RestoreProgress progress, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeElevationService : IElevationService
    {
        public Task<RestoreActionExecutionResult> ExecuteAdministratorActionAsync(
            RestorePlan approvedPlan,
            RestoreAction action,
            IRestoreExecutionContext context,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Elevation was not expected in this test.");
        }
    }
}
