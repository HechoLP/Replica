using Replica.Core.Diffing;
using Replica.Core.Execution;
using Replica.Core.Planning;
using Replica.Core.Recovery;
using Replica.Core.Services;
using Replica.Core.Snapshots;

namespace Replica.Core.Tests;

public sealed class RecoveryWizardServiceTests
{
    [Fact]
    public async Task SameComputerRecovery_CompletesAndShowsBeforeAndAfterScores()
    {
        TestContext context = new();

        RecoveryWizardSession session = await context.StartAnalyzeApproveExecuteAsync();

        Assert.Equal(RecoveryWizardStatus.Completed, session.Status);
        Assert.Equal(82, session.SimilarityBefore);
        Assert.Equal(100, session.SimilarityAfter);
        Assert.Contains("install", session.CompletedActionIds);
    }

    [Fact]
    public async Task DifferentHardware_IsReportedWithoutDriverRestoreAction()
    {
        TestContext context = new();
        context.Runtime.Analysis = context.Runtime.Analysis with
        {
            HardwareDifferences =
            [
                new RecoveryHardwareDifference(
                    "GPU",
                    "RTX 5070 Ti",
                    "RTX 6090",
                    "GPUChanged",
                    "Install the new GPU's official driver manually.",
                    true),
            ],
        };
        RecoveryWizardSession started = await context.StartAsync();

        RecoveryWizardSession analyzed = await context.Service.AnalyzeAsync(
            started.SessionId,
            [],
            ReadOnlyMemory<char>.Empty,
            default);

        RecoveryHardwareDifference difference = Assert.Single(analyzed.HardwareDifferences);
        Assert.Equal("GPUChanged", difference.ReasonCode);
        Assert.DoesNotContain(
            analyzed.Plan!.Actions,
            action => action.Name.Contains("driver", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DifferentDrive_PreservesUserMappingAndConflictChoice()
    {
        TestContext context = new();
        RecoveryWizardSession started = await context.StartAsync();
        RecoveryPathMapping mapping = new(
            "D:\\Projects",
            "C:\\Projects",
            RecoveryPathMappingScope.Project,
            RestoreFileConflictBehavior.RenameAndKeepBoth,
            1024);

        RecoveryWizardSession analyzed = await context.Service.AnalyzeAsync(
            started.SessionId,
            [mapping],
            ReadOnlyMemory<char>.Empty,
            default);

        Assert.Equal(mapping, Assert.Single(analyzed.PathMappings));
        Assert.Equal(mapping, Assert.Single(context.Runtime.LastMappings));
    }

    [Fact]
    public async Task RestartResume_RequiresApprovalAndNeverRepeatsCompletedActions()
    {
        TestContext context = new();
        context.Runtime.Execution = new RecoveryExecutionBatch(
            [Result("install", RestoreExecutionState.RequiresRestart)],
            RequiresRestart: true,
            WasCancelled: false);
        RecoveryWizardSession started = await context.StartAsync();
        RecoveryWizardSession analyzed = await context.Service.AnalyzeAsync(
            started.SessionId, [], ReadOnlyMemory<char>.Empty, default);
        await context.Service.ApprovePlanAsync(analyzed.SessionId, default);
        RecoveryWizardSession waiting = await context.Service.ExecuteAsync(
            analyzed.SessionId, ReadOnlyMemory<char>.Empty, default);

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Service.ApproveRestartAsync(
            waiting.SessionId, false, default));
        RecoveryWizardSession approved = await context.Service.ApproveRestartAsync(
            waiting.SessionId, true, default);
        RecoveryWizardSession? resumed = await context.Service.LoadForResumeAsync(
            approved.SessionId, default);
        RecoveryWizardSession completed = await context.Service.ConfirmResumeAsync(
            resumed!.SessionId, ReadOnlyMemory<char>.Empty, default);

        Assert.Equal(1, context.Startup.RegisterCount);
        Assert.True(context.Startup.LastApproval);
        Assert.Equal(1, context.Startup.UnregisterCount);
        Assert.Equal(RecoveryWizardStatus.Completed, completed.Status);
        Assert.Single(context.Runtime.ExecutionCalls);
        Assert.Contains("install", completed.CompletedActionIds);
    }

    [Fact]
    public async Task PartialFailure_RetriesOnlyFailedAction()
    {
        TestContext context = new();
        context.Runtime.Execution = new RecoveryExecutionBatch(
            [
                Result("install", RestoreExecutionState.Succeeded),
                Result("file", RestoreExecutionState.Failed),
            ],
            false,
            false);
        RecoveryWizardSession first = await context.StartAnalyzeApproveExecuteAsync();
        context.Runtime.Execution = new RecoveryExecutionBatch(
            [Result("file", RestoreExecutionState.Succeeded)],
            false,
            false);

        RecoveryWizardSession retried = await context.Service.RetryFailedAsync(
            first.SessionId, ReadOnlyMemory<char>.Empty, default);

        Assert.Equal(["file"], context.Runtime.ExecutionCalls[1].RetryIds!);
        Assert.Contains("install", context.Runtime.ExecutionCalls[1].CompletedIds);
        Assert.Empty(retried.FailedActionIds);
    }

    [Fact]
    public async Task CorruptSnapshot_IsRejected()
    {
        TestContext context = new();
        context.Runtime.OpenException = new ReplicaSnapshotException("corrupt");

        await Assert.ThrowsAsync<ReplicaSnapshotException>(() => context.Service.StartAsync(
            "corrupt.replica", ReadOnlyMemory<char>.Empty, default));

        Assert.Empty(context.Store.Sessions);
    }

    [Fact]
    public async Task EncryptedSnapshot_RequiresPasswordAndNeverPersistsIt()
    {
        TestContext context = new();
        context.Runtime.RequiresPassword = true;

        RecoveryStartResult first = await context.Service.StartAsync(
            "encrypted.replica", ReadOnlyMemory<char>.Empty, default);
        char[] password = "correct horse".ToCharArray();
        RecoveryStartResult second = await context.Service.StartAsync(
            "encrypted.replica", password, default);

        Assert.True(first.PasswordRequired);
        Assert.NotNull(second.Session);
        Assert.True(second.Session.SnapshotEncrypted);
        Assert.DoesNotContain("correct horse", context.Store.SerializedView, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InsufficientStorage_StopsBeforeApproval()
    {
        TestContext context = new();
        context.Runtime.Analysis = context.Runtime.Analysis with
        {
            HasSufficientStorage = false,
            RequiredBytes = 10_000,
            AvailableBytes = 100,
        };
        RecoveryWizardSession started = await context.StartAsync();

        RecoveryWizardSession failed = await context.Service.AnalyzeAsync(
            started.SessionId, [], ReadOnlyMemory<char>.Empty, default);

        Assert.Equal(RecoveryWizardStatus.Failed, failed.Status);
        Assert.Equal("InsufficientStorage", failed.FailureReasonCode);
        Assert.Null(failed.Plan);
    }

    [Fact]
    public async Task UserCancellation_IsPersistedAndStartupRegistrationRemoved()
    {
        TestContext context = new();
        RecoveryWizardSession started = await context.StartAsync();

        RecoveryWizardSession cancelled = await context.Service.CancelAsync(
            started.SessionId, default);

        Assert.Equal(RecoveryWizardStatus.Cancelled, cancelled.Status);
        Assert.Equal("UserCancelled", cancelled.FailureReasonCode);
        Assert.Equal(1, context.Startup.UnregisterCount);
    }

    [Theory]
    [InlineData("--resume-recovery", "bad", false)]
    [InlineData("--other", "0f8fad5bd9cb469fa16570867728950e", false)]
    [InlineData("--resume-recovery", "0f8fad5bd9cb469fa16570867728950e", true)]
    public void ResumeArguments_AreStrictlyTyped(string option, string id, bool expected)
    {
        bool result = RecoveryResumeArgumentsParser.TryParse([option, id], out string? sessionId);

        Assert.Equal(expected, result);
        Assert.Equal(expected ? id : null, sessionId);
    }

    private static RestoreActionExecutionResult Result(
        string id,
        RestoreExecutionState state) => new(
        id,
        state,
        state.ToString(),
        state.ToString(),
        RequiresRestart: state == RestoreExecutionState.RequiresRestart);

    private sealed class TestContext
    {
        public TestContext()
        {
            Service = new RecoveryWizardService(Runtime, Store, Startup, TimeProvider.System);
        }

        public FakeRuntime Runtime { get; } = new();

        public MemoryStore Store { get; } = new();

        public FakeStartupRegistrar Startup { get; } = new();

        public RecoveryWizardService Service { get; }

        public async Task<RecoveryWizardSession> StartAsync()
        {
            RecoveryStartResult result = await Service.StartAsync(
                "recovery.replica", ReadOnlyMemory<char>.Empty, default);
            return result.Session!;
        }

        public async Task<RecoveryWizardSession> StartAnalyzeApproveExecuteAsync()
        {
            RecoveryWizardSession started = await StartAsync();
            RecoveryWizardSession analyzed = await Service.AnalyzeAsync(
                started.SessionId, [], ReadOnlyMemory<char>.Empty, default);
            await Service.ApprovePlanAsync(analyzed.SessionId, default);
            return await Service.ExecuteAsync(
                analyzed.SessionId, ReadOnlyMemory<char>.Empty, default);
        }
    }

    private sealed class FakeRuntime : IRecoveryWizardRuntime
    {
        private static readonly RestorePlan Plan = CreatePlan();

        public Exception? OpenException { get; set; }

        public bool RequiresPassword { get; set; }

        public RecoveryAnalysisResult Analysis { get; set; } = new(
            Plan,
            82,
            [],
            [],
            [new RecoveryManualAction("microsoft", "Microsoft login", "Sign in manually.", "AuthenticationNotRestored")],
            true,
            0,
            1);

        public RecoveryExecutionBatch Execution { get; set; } = new(
            [Result("install", RestoreExecutionState.Succeeded), Result("file", RestoreExecutionState.Succeeded)],
            false,
            false);

        public IReadOnlyList<RecoveryPathMapping> LastMappings { get; private set; } = [];

        public List<ExecutionCall> ExecutionCalls { get; } = [];

        public Task<RecoveryPreparedSnapshot> OpenSnapshotAsync(
            string snapshotPath,
            ReadOnlyMemory<char> password,
            CancellationToken cancellationToken)
        {
            if (OpenException is not null)
            {
                throw OpenException;
            }

            if (RequiresPassword && password.IsEmpty)
            {
                throw new ReplicaSnapshotDecryptionException();
            }

            return Task.FromResult(new RecoveryPreparedSnapshot(
                Guid.NewGuid(),
                SnapshotType.Recovery,
                "OLD-PC",
                "11 24H2",
                "x64",
                "ko-KR",
                DateTimeOffset.UtcNow,
                RequiresPassword,
                []));
        }

        public Task<RecoveryAnalysisResult> AnalyzeAsync(
            string snapshotPath,
            IReadOnlyList<RecoveryPathMapping> mappings,
            ReadOnlyMemory<char> password,
            CancellationToken cancellationToken)
        {
            LastMappings = mappings;
            return Task.FromResult(Analysis with { PathMappings = mappings });
        }

        public Task<RecoveryExecutionBatch> ExecuteAsync(
            string sessionId,
            string snapshotPath,
            RestorePlan approvedPlan,
            IReadOnlyList<RecoveryPathMapping> mappings,
            IReadOnlySet<string> completedActionIds,
            IReadOnlySet<string>? retryActionIds,
            ReadOnlyMemory<char> password,
            CancellationToken cancellationToken)
        {
            ExecutionCalls.Add(new ExecutionCall(
                completedActionIds.Order(StringComparer.Ordinal).ToArray(),
                retryActionIds?.Order(StringComparer.Ordinal).ToArray()));
            return Task.FromResult(Execution);
        }

        public Task<RecoveryVerificationResult> VerifyAsync(
            string snapshotPath,
            IReadOnlyList<RecoveryPathMapping> mappings,
            ReadOnlyMemory<char> password,
            CancellationToken cancellationToken) => Task.FromResult(
                new RecoveryVerificationResult(100, Analysis.ManualActions));

        private static RestorePlan CreatePlan()
        {
            RestoreAction install = Action("install", RestoreActionType.InstallPackage, []);
            RestoreAction file = Action("file", RestoreActionType.RestoreSelectedUserFile, ["install"]);
            return new RestorePlan(
                "plan",
                DiffRestoreMode.Recommended,
                [install, file],
                new RestoreDryRunSummary(1, 0, 0, 1, 0, 0, false, 1, 0, 2, TimeSpan.Zero, 0),
                RestorePlanReviewStatus.PendingReview,
                DateTimeOffset.UtcNow);
        }

        private static RestoreAction Action(
            string id,
            RestoreActionType type,
            IReadOnlyList<string> dependencies) => new(
            id,
            type,
            id,
            id,
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
            type == RestoreActionType.InstallPackage ? DiffArea.Applications : DiffArea.SelectedUserFiles,
            id,
            "Missing");
    }

    private sealed record ExecutionCall(string[] CompletedIds, string[]? RetryIds);

    private sealed class MemoryStore : IRecoverySessionStore
    {
        public Dictionary<string, RecoveryWizardSession> Sessions { get; } = new(StringComparer.Ordinal);

        public string SerializedView => string.Join("|", Sessions.Values.Select(session => session.ToString()));

        public Task SaveAsync(RecoveryWizardSession session, CancellationToken cancellationToken)
        {
            Sessions[session.SessionId] = session;
            return Task.CompletedTask;
        }

        public Task<RecoveryWizardSession?> LoadAsync(
            string sessionId,
            CancellationToken cancellationToken)
        {
            Sessions.TryGetValue(sessionId, out RecoveryWizardSession? session);
            return Task.FromResult(session);
        }
    }

    private sealed class FakeStartupRegistrar : IRecoveryStartupRegistrar
    {
        public int RegisterCount { get; private set; }

        public int UnregisterCount { get; private set; }

        public bool LastApproval { get; private set; }

        public Task RegisterAsync(
            string sessionId,
            bool userApproved,
            CancellationToken cancellationToken)
        {
            RegisterCount++;
            LastApproval = userApproved;
            return Task.CompletedTask;
        }

        public Task UnregisterAsync(string sessionId, CancellationToken cancellationToken)
        {
            UnregisterCount++;
            return Task.CompletedTask;
        }
    }
}
