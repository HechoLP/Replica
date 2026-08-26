using Replica.Core.Execution;
using Replica.Core.Planning;
using Replica.Core.Rollback;
using Replica.Core.Services;
using Replica.Infrastructure.Restore;

namespace Replica.Infrastructure.Tests.Restore;

public sealed class RestoreActionHandlerTests
{
    [Fact]
    public async Task PackageHandler_BlocksElevatedExecutionBeforeInvokingWinget()
    {
        CapturingWinGetInstaller installer = new();
        PackageRestoreActionHandler handler = new(installer);
        RestoreAction action = new(
            "package-action",
            RestoreActionType.InstallPackage,
            "Editor",
            "Install editor.",
            null,
            null,
            "1.0.0",
            Replica.Core.Diffing.DiffRiskLevel.Low,
            true,
            false,
            false,
            [],
            TimeSpan.Zero,
            0,
            true,
            false,
            Replica.Core.Diffing.DiffArea.Applications,
            "Example.Editor",
            "Fixture",
            Replica.Core.Matching.ApplicationMatchConfidence.Exact);

        RestoreActionExecutionResult result = await handler.ExecuteAsync(
            action,
            new RestoreExecutionContext("session", true, new NullJournal()),
            CancellationToken.None);

        Assert.Equal(RestoreExecutionState.Failed, result.State);
        Assert.Equal("ElevatedPackageExecutionBlocked", result.ReasonCode);
        Assert.Null(installer.Request);
    }

    [Fact]
    public async Task EnvironmentHandler_StripsValidatedScopeFromDiffKey()
    {
        CapturingEnvironmentWriter writer = new();
        EnvironmentRestoreActionHandler handler = new(writer);
        RestoreAction action = new(
            "environment-action",
            RestoreActionType.SetMachineEnvironmentVariable,
            "Editor",
            "Restore editor setting.",
            null,
            null,
            "code",
            Replica.Core.Diffing.DiffRiskLevel.Low,
            true,
            false,
            true,
            [],
            TimeSpan.Zero,
            0,
            true,
            false,
            Replica.Core.Diffing.DiffArea.EnvironmentVariables,
            "Machine:EDITOR",
            "Fixture");

        await handler.ExecuteAsync(
            action,
            new RestoreExecutionContext("session", true, new NullJournal()),
            CancellationToken.None);

        Assert.NotNull(writer.Request);
        Assert.Equal("EDITOR", writer.Request.Name);
        Assert.Equal(EnvironmentVariableScope.Machine, writer.Request.Scope);
    }

    private sealed class CapturingEnvironmentWriter : IEnvironmentWriter
    {
        public EnvironmentWriteRequest? Request { get; private set; }

        public Task<EnvironmentWriteResult> WriteAsync(
            EnvironmentWriteRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            return Task.FromResult(new EnvironmentWriteResult(
                RestoreExecutionState.Succeeded,
                "Fixture",
                true,
                false));
        }
    }

    private sealed class CapturingWinGetInstaller : IWinGetInstaller
    {
        public WinGetInstallRequest? Request { get; private set; }

        public Task<WinGetInstallResult> InstallAsync(
            WinGetInstallRequest request,
            CancellationToken cancellationToken)
        {
            Request = request;
            throw new InvalidOperationException("WinGet must not be invoked from an elevated context.");
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
}
