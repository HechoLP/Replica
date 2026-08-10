using Replica.Core.Execution;
using Replica.Core.History;
using Replica.Core.Planning;
using Replica.Core.Services;
using Replica.Core.Snapshots;

namespace Replica.Core.Recovery;

public sealed class RecoveryWizardService : IRecoveryWizardService
{
    private readonly IRecoveryWizardRuntime _runtime;
    private readonly IRecoverySessionStore _sessionStore;
    private readonly IRecoveryStartupRegistrar _startupRegistrar;
    private readonly ISnapshotHistoryService? _snapshotHistory;
    private readonly TimeProvider _timeProvider;

    public RecoveryWizardService(
        IRecoveryWizardRuntime runtime,
        IRecoverySessionStore sessionStore,
        IRecoveryStartupRegistrar startupRegistrar,
        TimeProvider timeProvider,
        ISnapshotHistoryService? snapshotHistory = null)
    {
        _runtime = runtime;
        _sessionStore = sessionStore;
        _startupRegistrar = startupRegistrar;
        _timeProvider = timeProvider;
        _snapshotHistory = snapshotHistory;
    }

    public async Task<RecoveryStartResult> StartAsync(
        string snapshotPath,
        ReadOnlyMemory<char> password,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotPath);
        RecoveryPreparedSnapshot prepared;
        try
        {
            prepared = await _runtime.OpenSnapshotAsync(
                snapshotPath,
                password,
                cancellationToken).ConfigureAwait(false);
        }
        catch (ReplicaSnapshotDecryptionException) when (password.IsEmpty)
        {
            return new RecoveryStartResult(null, PasswordRequired: true);
        }

        if (prepared.SnapshotType is not (SnapshotType.Recovery or SnapshotType.OfflineRecoveryPack))
        {
            throw new ReplicaSnapshotException(
                "Post-reset recovery requires a Recovery Snapshot or Offline Recovery Pack.");
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        RecoveryWizardSession session = new(
            Guid.NewGuid().ToString("N"),
            Path.GetFullPath(snapshotPath),
            prepared.SnapshotId,
            RecoveryWizardStatus.InProgress,
            RecoveryWizardStep.ShowSourceComputer,
            prepared.SourceMachineName,
            prepared.WindowsVersion,
            prepared.Architecture,
            prepared.Locale,
            prepared.IsEncrypted,
            null,
            prepared.SuggestedMappings,
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
        await _sessionStore.SaveAsync(session, cancellationToken).ConfigureAwait(false);
        return new RecoveryStartResult(session, PasswordRequired: false);
    }

    public async Task<RecoveryWizardSession> AnalyzeAsync(
        string sessionId,
        IReadOnlyList<RecoveryPathMapping> mappings,
        ReadOnlyMemory<char> password,
        CancellationToken cancellationToken)
    {
        RecoveryWizardSession session = await RequireSessionAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);
        EnsureStatus(session, RecoveryWizardStatus.InProgress);
        ValidateMappings(mappings);
        RecoveryAnalysisResult analysis = await _runtime.AnalyzeAsync(
            session.SnapshotPath,
            mappings,
            password,
            cancellationToken).ConfigureAwait(false);
        if (!analysis.HasSufficientStorage)
        {
            RecoveryWizardSession failed = Touch(session with
            {
                Status = RecoveryWizardStatus.Failed,
                CurrentStep = RecoveryWizardStep.ConfirmFileDestinations,
                PathMappings = analysis.PathMappings,
                HardwareDifferences = analysis.HardwareDifferences,
                SimilarityBefore = analysis.SimilarityBefore,
                FailureReasonCode = "InsufficientStorage",
            });
            await _sessionStore.SaveAsync(failed, cancellationToken).ConfigureAwait(false);
            return failed;
        }

        RecoveryWizardSession analyzed = Touch(session with
        {
            Status = RecoveryWizardStatus.AwaitingApproval,
            CurrentStep = RecoveryWizardStep.FinalApproval,
            Plan = analysis.Plan,
            PathMappings = analysis.PathMappings,
            HardwareDifferences = analysis.HardwareDifferences,
            ManualActions = analysis.ManualActions,
            SimilarityBefore = analysis.SimilarityBefore,
            FailureReasonCode = null,
        });
        await _sessionStore.SaveAsync(analyzed, cancellationToken).ConfigureAwait(false);
        return analyzed;
    }

    public async Task<RecoveryWizardSession> ApprovePlanAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        RecoveryWizardSession session = await RequireSessionAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);
        EnsureStatus(session, RecoveryWizardStatus.AwaitingApproval);
        RestorePlan plan = session.Plan ?? throw new InvalidOperationException(
            "The recovery restore plan is missing.");
        RecoveryWizardSession approved = Touch(session with
        {
            Status = RecoveryWizardStatus.InProgress,
            CurrentStep = RecoveryWizardStep.InstallApplications,
            Plan = plan.Approve(),
        });
        await _sessionStore.SaveAsync(approved, cancellationToken).ConfigureAwait(false);
        return approved;
    }

    public Task<RecoveryWizardSession> ExecuteAsync(
        string sessionId,
        ReadOnlyMemory<char> password,
        CancellationToken cancellationToken)
    {
        return ExecuteCoreAsync(sessionId, password, retryFailed: false, cancellationToken);
    }

    public Task<RecoveryWizardSession> RetryFailedAsync(
        string sessionId,
        ReadOnlyMemory<char> password,
        CancellationToken cancellationToken)
    {
        return ExecuteCoreAsync(sessionId, password, retryFailed: true, cancellationToken);
    }

    public async Task<RecoveryWizardSession> ApproveRestartAsync(
        string sessionId,
        bool userApproved,
        CancellationToken cancellationToken)
    {
        RecoveryWizardSession session = await RequireSessionAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);
        EnsureStatus(session, RecoveryWizardStatus.AwaitingRestartApproval);
        if (!userApproved)
        {
            throw new InvalidOperationException("Recovery restart registration requires explicit approval.");
        }

        RecoveryWizardSession approved = Touch(session with
        {
            RestartApproved = true,
            CurrentStep = RecoveryWizardStep.ResumeAfterRestart,
        });
        await _sessionStore.SaveAsync(approved, cancellationToken).ConfigureAwait(false);
        await _startupRegistrar.RegisterAsync(sessionId, true, cancellationToken).ConfigureAwait(false);
        return approved;
    }

    public async Task<RecoveryWizardSession?> LoadForResumeAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        RecoveryWizardSession? session = await _sessionStore.LoadAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);
        if (session is null || !session.RestartApproved || !session.RequiresRestart)
        {
            return null;
        }

        RecoveryWizardSession awaiting = Touch(session with
        {
            Status = RecoveryWizardStatus.AwaitingResumeConfirmation,
            CurrentStep = RecoveryWizardStep.ResumeAfterRestart,
        });
        await _sessionStore.SaveAsync(awaiting, cancellationToken).ConfigureAwait(false);
        return awaiting;
    }

    public async Task<RecoveryWizardSession> ConfirmResumeAsync(
        string sessionId,
        ReadOnlyMemory<char> password,
        CancellationToken cancellationToken)
    {
        RecoveryWizardSession session = await RequireSessionAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);
        EnsureStatus(session, RecoveryWizardStatus.AwaitingResumeConfirmation);
        await _startupRegistrar.UnregisterAsync(sessionId, cancellationToken).ConfigureAwait(false);
        RecoveryWizardSession resumed = Touch(session with
        {
            Status = RecoveryWizardStatus.InProgress,
            CurrentStep = RecoveryWizardStep.FinalValidation,
            RestartApproved = false,
            RequiresRestart = false,
        });
        await _sessionStore.SaveAsync(resumed, cancellationToken).ConfigureAwait(false);
        return await VerifyAsync(resumed, password, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RecoveryWizardSession> CancelAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        RecoveryWizardSession session = await RequireSessionAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);
        await _startupRegistrar.UnregisterAsync(sessionId, cancellationToken).ConfigureAwait(false);
        RecoveryWizardSession cancelled = Touch(session with
        {
            Status = RecoveryWizardStatus.Cancelled,
            FailureReasonCode = "UserCancelled",
        });
        await _sessionStore.SaveAsync(cancelled, cancellationToken).ConfigureAwait(false);
        return cancelled;
    }

    private async Task<RecoveryWizardSession> ExecuteCoreAsync(
        string sessionId,
        ReadOnlyMemory<char> password,
        bool retryFailed,
        CancellationToken cancellationToken)
    {
        RecoveryWizardSession session = await RequireSessionAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);
        if (retryFailed)
        {
            if (!session.CanRetry)
            {
                throw new InvalidOperationException("The recovery session has no failed actions to retry.");
            }
        }
        else
        {
            EnsureStatus(session, RecoveryWizardStatus.InProgress);
        }

        RestorePlan plan = session.Plan ?? throw new InvalidOperationException(
            "The approved recovery restore plan is missing.");
        if (!plan.IsApproved)
        {
            throw new InvalidOperationException("The recovery restore plan is not approved.");
        }

        RecoveryWizardSession executing = Touch(session with
        {
            Status = RecoveryWizardStatus.Executing,
        });
        await _sessionStore.SaveAsync(executing, cancellationToken).ConfigureAwait(false);
        HashSet<string>? retryIds = retryFailed
            ? session.FailedActionIds.ToHashSet(StringComparer.Ordinal)
            : null;
        RecoveryExecutionBatch batch = await _runtime.ExecuteAsync(
            session.SessionId,
            session.SnapshotPath,
            plan,
            session.PathMappings,
            session.CompletedActionIds.ToHashSet(StringComparer.Ordinal),
            retryIds,
            password,
            cancellationToken).ConfigureAwait(false);
        RecoveryWizardSession updated = MergeExecution(session, batch);
        await _sessionStore.SaveAsync(updated, CancellationToken.None).ConfigureAwait(false);
        if (updated.Status == RecoveryWizardStatus.Cancelled ||
            updated.Status == RecoveryWizardStatus.AwaitingRestartApproval)
        {
            return updated;
        }

        return await VerifyAsync(updated, password, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RecoveryWizardSession> VerifyAsync(
        RecoveryWizardSession session,
        ReadOnlyMemory<char> password,
        CancellationToken cancellationToken)
    {
        RecoveryWizardSession verifying = Touch(session with
        {
            Status = RecoveryWizardStatus.Verifying,
            CurrentStep = RecoveryWizardStep.FinalValidation,
        });
        await _sessionStore.SaveAsync(verifying, cancellationToken).ConfigureAwait(false);
        RecoveryVerificationResult verification = await _runtime.VerifyAsync(
            session.SnapshotPath,
            session.PathMappings,
            password,
            cancellationToken).ConfigureAwait(false);
        RecoveryWizardSession completed = Touch(verifying with
        {
            Status = RecoveryWizardStatus.Completed,
            CurrentStep = RecoveryWizardStep.ShowManualActions,
            SimilarityAfter = verification.SimilarityAfter,
            ManualActions = session.ManualActions
                .Concat(verification.ManualActions)
                .DistinctBy(action => action.Id, StringComparer.Ordinal)
                .ToArray(),
        });
        await _sessionStore.SaveAsync(completed, cancellationToken).ConfigureAwait(false);
        await TryRecordRestoreHistoryAsync(completed, cancellationToken).ConfigureAwait(false);
        return completed;
    }

    private async Task TryRecordRestoreHistoryAsync(
        RecoveryWizardSession session,
        CancellationToken cancellationToken)
    {
        if (_snapshotHistory is null)
        {
            return;
        }

        try
        {
            await _snapshotHistory.RecordRestoreAsync(
                new RestoreHistoryRecord(
                    session.SessionId,
                    session.SnapshotId,
                    session.CreatedAtUtc,
                    session.UpdatedAtUtc,
                    session.Status.ToString(),
                    session.ActionResults.Count(result => result.State is
                        RestoreExecutionState.Succeeded or RestoreExecutionState.RequiresRestart),
                    session.ActionResults.Count(result => result.State == RestoreExecutionState.Failed),
                    session.ActionResults.Count(result => result.State == RestoreExecutionState.Skipped),
                    session.SimilarityBefore,
                    session.SimilarityAfter),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException and not OutOfMemoryException)
        {
            // A local history-index failure must not turn a completed restore into a failed mutation.
        }
    }

    private RecoveryWizardSession MergeExecution(
        RecoveryWizardSession session,
        RecoveryExecutionBatch batch)
    {
        Dictionary<string, RestoreActionExecutionResult> merged = session.ActionResults
            .ToDictionary(result => result.ActionId, StringComparer.Ordinal);
        foreach (RestoreActionExecutionResult result in batch.Actions)
        {
            merged[result.ActionId] = result;
        }

        string[] completed = merged.Values
            .Where(result => result.State is
                RestoreExecutionState.Succeeded or
                RestoreExecutionState.RequiresRestart ||
                result.State == RestoreExecutionState.Skipped &&
                result.ReasonCode is "ActionNotSelected" or "ManualActionRequired")
            .Select(result => result.ActionId)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] failed = merged.Values
            .Where(result => result.State == RestoreExecutionState.Failed ||
                result.State == RestoreExecutionState.Skipped &&
                result.ReasonCode == "DependencyNotSucceeded")
            .Select(result => result.ActionId)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return Touch(session with
        {
            Status = batch.WasCancelled
                ? RecoveryWizardStatus.Cancelled
                : batch.RequiresRestart
                    ? RecoveryWizardStatus.AwaitingRestartApproval
                    : RecoveryWizardStatus.InProgress,
            CurrentStep = batch.RequiresRestart
                ? RecoveryWizardStep.ShowRestartRequirements
                : RecoveryWizardStep.FinalValidation,
            ActionResults = merged.Values.OrderBy(result => result.ActionId, StringComparer.Ordinal).ToArray(),
            CompletedActionIds = completed,
            FailedActionIds = failed,
            RequiresRestart = batch.RequiresRestart,
            FailureReasonCode = batch.WasCancelled ? "UserCancelled" : null,
        });
    }

    private async Task<RecoveryWizardSession> RequireSessionAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(sessionId, "N", out _))
        {
            throw new ArgumentException("The recovery session identifier is invalid.", nameof(sessionId));
        }

        return await _sessionStore.LoadAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The recovery session does not exist.");
    }

    private RecoveryWizardSession Touch(RecoveryWizardSession session)
    {
        return session with { UpdatedAtUtc = _timeProvider.GetUtcNow() };
    }

    private static void EnsureStatus(
        RecoveryWizardSession session,
        RecoveryWizardStatus expected)
    {
        if (session.Status != expected)
        {
            throw new InvalidOperationException(
                $"The recovery session is in '{session.Status}', not '{expected}'.");
        }
    }

    private static void ValidateMappings(IReadOnlyList<RecoveryPathMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        HashSet<string> sources = new(StringComparer.OrdinalIgnoreCase);
        foreach (RecoveryPathMapping mapping in mappings)
        {
            if (mapping is null ||
                !Path.IsPathFullyQualified(mapping.SourcePath) ||
                !Path.IsPathFullyQualified(mapping.TargetPath) ||
                mapping.EstimatedBytes < 0 ||
                !Enum.IsDefined(mapping.Scope) ||
                !Enum.IsDefined(mapping.ConflictBehavior) ||
                !sources.Add(Path.GetFullPath(mapping.SourcePath)))
            {
                throw new ArgumentException("A recovery path mapping is invalid.", nameof(mappings));
            }
        }
    }
}
