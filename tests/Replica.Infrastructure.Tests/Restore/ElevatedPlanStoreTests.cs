using Replica.Core.Diffing;
using Replica.Core.Execution;
using Replica.Core.Matching;
using Replica.Core.Planning;
using Replica.Core.Rollback;
using Replica.Core.Services;
using Replica.Infrastructure.Paths;
using Replica.Infrastructure.Restore;

namespace Replica.Infrastructure.Tests.Restore;

public sealed class ElevatedPlanStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"ReplicaElevatedPlanTests-{Guid.NewGuid():N}");

    [Fact]
    public async Task ConsumeAsync_RejectsTamperedAdministratorPlanAndDeletesIt()
    {
        MutableTimeProvider time = new(new DateTimeOffset(2026, 8, 5, 0, 0, 0, TimeSpan.Zero));
        ElevatedPlanStore store = CreateStore(time);
        FakeContext context = new("session-tamper");
        RestoreAction action = AdministratorAction("admin-tamper");
        ElevatedPlanCreationResult created = await store.CreateAsync(
            Plan(action),
            action,
            context,
            CancellationToken.None);
        await File.AppendAllTextAsync(created.LaunchRequest.PlanFilePath, " ");

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ConsumeAsync(
            Arguments(created.LaunchRequest),
            CancellationToken.None));

        Assert.False(File.Exists(created.LaunchRequest.PlanFilePath));
    }

    [Fact]
    public async Task ConsumeAsync_RejectsExpiredAdministratorPlanAndDeletesIt()
    {
        MutableTimeProvider time = new(new DateTimeOffset(2026, 8, 5, 0, 0, 0, TimeSpan.Zero));
        ElevatedPlanStore store = CreateStore(time);
        FakeContext context = new("session-expired");
        RestoreAction action = AdministratorAction("admin-expired");
        ElevatedPlanCreationResult created = await store.CreateAsync(
            Plan(action),
            action,
            context,
            CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(6));

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ConsumeAsync(
            Arguments(created.LaunchRequest),
            CancellationToken.None));

        Assert.False(File.Exists(created.LaunchRequest.PlanFilePath));
    }

    [Fact]
    public async Task ConsumeAsync_RejectsSingleUseTokenReplay()
    {
        MutableTimeProvider time = new(new DateTimeOffset(2026, 8, 5, 0, 0, 0, TimeSpan.Zero));
        ElevatedPlanStore store = CreateStore(time);
        FakeContext context = new("session-replay");
        RestoreAction action = AdministratorAction("admin-replay");
        ElevatedPlanCreationResult created = await store.CreateAsync(
            Plan(action),
            action,
            context,
            CancellationToken.None);
        string replayPath = Path.Combine(
            Path.GetDirectoryName(created.LaunchRequest.PlanFilePath)!,
            $"replay-{Guid.NewGuid():N}.json");
        File.Copy(created.LaunchRequest.PlanFilePath, replayPath);

        ElevatedRestorePlanEnvelope envelope = await store.ConsumeAsync(
            Arguments(created.LaunchRequest),
            CancellationToken.None);
        ElevatedExecutorArguments replay = Arguments(created.LaunchRequest) with
        {
            PlanFilePath = replayPath,
        };

        Assert.Equal(action.Id, Assert.Single(envelope.Plan.Actions).Id);
        await Assert.ThrowsAsync<IOException>(() => store.ConsumeAsync(
            replay,
            CancellationToken.None));
        Assert.False(File.Exists(replayPath));
    }

    [Fact]
    public async Task ConsumeAsync_AllowsOnlyOneConcurrentConsumer()
    {
        MutableTimeProvider time = new(new DateTimeOffset(2026, 8, 5, 0, 0, 0, TimeSpan.Zero));
        ElevatedPlanStore store = CreateStore(time);
        FakeContext context = new("session-race");
        RestoreAction action = AdministratorAction("admin-race");
        ElevatedPlanCreationResult created = await store.CreateAsync(
            Plan(action),
            action,
            context,
            CancellationToken.None);

        bool[] results = await Task.WhenAll(
            TryConsumeAsync(store, created.LaunchRequest),
            TryConsumeAsync(store, created.LaunchRequest));

        Assert.Equal(1, results.Count(result => result));
        Assert.Equal(1, results.Count(result => !result));
        Assert.False(File.Exists(created.LaunchRequest.PlanFilePath));
    }

    [Fact]
    public void ElevatedExecutorArgumentsParser_RequiresExactRestrictedShape()
    {
        string[] valid =
        [
            "--elevated-executor",
            "plan.json",
            "--plan-sha256",
            new string('A', 64),
            "--single-use-token",
            new string('B', 64),
            "--session",
            "session",
        ];

        Assert.True(ElevatedExecutorArgumentsParser.IsElevatedRequest(valid));
        Assert.True(ElevatedExecutorArgumentsParser.TryParse(valid, out ElevatedExecutorArguments? parsed));
        Assert.NotNull(parsed);
        Assert.False(ElevatedExecutorArgumentsParser.TryParse([.. valid, "--unexpected"], out _));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private ElevatedPlanStore CreateStore(TimeProvider timeProvider)
    {
        return new ElevatedPlanStore(new ReplicaPathProvider(_root), timeProvider);
    }

    private static RestoreAction AdministratorAction(string id)
    {
        return new RestoreAction(
            id,
            RestoreActionType.SetMachineEnvironmentVariable,
            "Machine setting",
            "test",
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
            "TEST_VALUE",
            "Test",
            ApplicationMatchConfidence.Exact);
    }

    private static RestorePlan Plan(RestoreAction action)
    {
        return new RestorePlan(
            "approved-plan",
            DiffRestoreMode.Safe,
            [action],
            new RestoreDryRunSummary(0, 0, 0, 0, 1, 1, false, 1, 0, 1, TimeSpan.Zero, 0),
            RestorePlanReviewStatus.Approved,
            DateTimeOffset.UtcNow);
    }

    private static ElevatedExecutorArguments Arguments(ElevatedPlanLaunchRequest request)
    {
        return new ElevatedExecutorArguments(
            request.PlanFilePath,
            request.PlanSha256,
            request.SingleUseToken,
            request.SessionId);
    }

    private static async Task<bool> TryConsumeAsync(
        ElevatedPlanStore store,
        ElevatedPlanLaunchRequest request)
    {
        try
        {
            _ = await store.ConsumeAsync(Arguments(request), CancellationToken.None);
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            return false;
        }
    }

    private sealed class FakeContext(string sessionId) : IRestoreExecutionContext
    {
        public string SessionId => sessionId;

        public bool IsElevated => false;

        public IRestoreJournal Journal { get; } = new FakeJournal();

        public FileRestoreRequest? GetFileRestoreRequest(string actionId) => null;
    }

    private sealed class FakeJournal : IRestoreJournal
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

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
    }
}
