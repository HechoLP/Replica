using System.Security.Cryptography;
using System.Text.Json;
using Replica.Core.Execution;
using Replica.Core.History;
using Replica.Core.Planning;
using Replica.Core.Services;
using Replica.Core.Snapshots;

namespace Replica.Core.Recovery;

public sealed class RecoveryWizardService : IRecoveryWizardService
{
    private const int ReviewBindingVersion = 2;
    private readonly IRecoveryWizardRuntime _runtime;
    private readonly IRecoverySessionStore _sessionStore;
    private readonly IRecoveryStartupRegistrar _startupRegistrar;
    private readonly ISnapshotHistoryService? _snapshotHistory;
    private readonly IOfflineInstallerExportService? _offlineInstallerExporter;
    private readonly TimeProvider _timeProvider;

    public RecoveryWizardService(
        IRecoveryWizardRuntime runtime,
        IRecoverySessionStore sessionStore,
        IRecoveryStartupRegistrar startupRegistrar,
        TimeProvider timeProvider,
        ISnapshotHistoryService? snapshotHistory = null,
        IOfflineInstallerExportService? offlineInstallerExporter = null)
    {
        _runtime = runtime;
        _sessionStore = sessionStore;
        _startupRegistrar = startupRegistrar;
        _timeProvider = timeProvider;
        _snapshotHistory = snapshotHistory;
        _offlineInstallerExporter = offlineInstallerExporter;
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
            now,
            SnapshotSha256: prepared.SnapshotSha256);
        await _sessionStore.SaveAsync(session, cancellationToken).ConfigureAwait(false);
        return new RecoveryStartResult(session, PasswordRequired: false);
    }

    public async Task<OfflineInstallerExportResult> ExportOfflineInstallersAsync(
        string sessionId,
        string destinationDirectory,
        ReadOnlyMemory<char> password,
        CancellationToken cancellationToken)
    {
        if (_offlineInstallerExporter is null)
        {
            throw new InvalidOperationException("Offline installer export is unavailable.");
        }

        RecoveryWizardSession session = await RequireSessionAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);
        return await _offlineInstallerExporter.ExportAsync(
            session.SnapshotPath,
            session.SnapshotId,
            session.SnapshotSha256,
            destinationDirectory,
            password,
            cancellationToken).ConfigureAwait(false);
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
        if (analysis.SnapshotId != session.SnapshotId ||
            !FixedHashEquals(analysis.SnapshotSha256, session.SnapshotSha256))
        {
            throw new ReplicaSnapshotException(
                "The recovery snapshot changed after the session was started. Review it again.");
        }

        if (!analysis.HasSufficientStorage)
        {
            RecoveryWizardSession failed = Touch(session with
            {
                Status = RecoveryWizardStatus.InProgress,
                CurrentStep = RecoveryWizardStep.ConfirmFileDestinations,
                Plan = null,
                PathMappings = analysis.PathMappings,
                HardwareDifferences = analysis.HardwareDifferences,
                SimilarityBefore = analysis.SimilarityBefore,
                FailureReasonCode = "InsufficientStorage",
                ReviewBindingSha256 = null,
                RequiredBytes = analysis.RequiredBytes,
                AvailableBytes = analysis.AvailableBytes,
            });
            await _sessionStore.SaveAsync(failed, cancellationToken).ConfigureAwait(false);
            return failed;
        }

        RecoveryWizardSession analyzedWithoutBinding = Touch(session with
        {
            Status = RecoveryWizardStatus.AwaitingApproval,
            CurrentStep = RecoveryWizardStep.FinalApproval,
            Plan = analysis.Plan,
            PathMappings = analysis.PathMappings,
            HardwareDifferences = analysis.HardwareDifferences,
            ManualActions = analysis.ManualActions,
            SimilarityBefore = analysis.SimilarityBefore,
            FailureReasonCode = null,
            ReviewBindingSha256 = null,
            RequiredBytes = analysis.RequiredBytes,
            AvailableBytes = analysis.AvailableBytes,
        });
        RecoveryWizardSession analyzed = analyzedWithoutBinding with
        {
            ReviewBindingSha256 = ComputeReviewBinding(analyzedWithoutBinding, analysis.Plan),
        };
        await _sessionStore.SaveAsync(analyzed, cancellationToken).ConfigureAwait(false);
        return analyzed;
    }

    public async Task<RecoveryWizardSession> ApprovePlanAsync(
        string sessionId,
        string reviewedBindingSha256,
        CancellationToken cancellationToken)
    {
        RecoveryWizardSession session = await RequireSessionAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);
        EnsureStatus(session, RecoveryWizardStatus.AwaitingApproval);
        RestorePlan plan = session.Plan ?? throw new InvalidOperationException(
            "The recovery restore plan is missing.");
        ValidateReviewBinding(session, plan, reviewedBindingSha256);
        RestorePlan approvedPlan = plan.Approve();
        RecoveryWizardSession approvedWithoutBinding = Touch(session with
        {
            Status = RecoveryWizardStatus.InProgress,
            CurrentStep = RecoveryWizardStep.InstallApplications,
            Plan = approvedPlan,
            ReviewBindingSha256 = null,
        });
        RecoveryWizardSession approved = approvedWithoutBinding with
        {
            ReviewBindingSha256 = ComputeReviewBinding(approvedWithoutBinding, approvedPlan),
        };
        await _sessionStore.SaveAsync(approved, cancellationToken).ConfigureAwait(false);
        return approved;
    }

    public Task<RecoveryWizardSession> ExecuteAsync(
        string sessionId,
        string reviewedBindingSha256,
        ReadOnlyMemory<char> password,
        CancellationToken cancellationToken)
    {
        return ExecuteCoreAsync(
            sessionId,
            reviewedBindingSha256,
            password,
            retryFailed: false,
            cancellationToken);
    }

    public Task<RecoveryWizardSession> RetryFailedAsync(
        string sessionId,
        string reviewedBindingSha256,
        ReadOnlyMemory<char> password,
        CancellationToken cancellationToken)
    {
        return ExecuteCoreAsync(
            sessionId,
            reviewedBindingSha256,
            password,
            retryFailed: true,
            cancellationToken);
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
        bool payloadDeleted = await _runtime.CleanupPayloadAsync(
            sessionId,
            CancellationToken.None).ConfigureAwait(false);
        RecoveryWizardSession cancelled = Touch(session with
        {
            Status = RecoveryWizardStatus.Cancelled,
            FailureReasonCode = payloadDeleted
                ? "UserCancelled"
                : "UserCancelledPayloadCleanupPending",
        });
        await _sessionStore.SaveAsync(cancelled, CancellationToken.None).ConfigureAwait(false);
        return cancelled;
    }

    private async Task<RecoveryWizardSession> ExecuteCoreAsync(
        string sessionId,
        string reviewedBindingSha256,
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

        ValidateReviewBinding(session, plan, reviewedBindingSha256);

        RecoveryWizardSession executing = Touch(session with
        {
            Status = RecoveryWizardStatus.Executing,
        });
        await _sessionStore.SaveAsync(executing, cancellationToken).ConfigureAwait(false);
        HashSet<string>? retryIds = retryFailed
            ? session.FailedActionIds.ToHashSet(StringComparer.Ordinal)
            : null;
        RecoveryExecutionBatch batch;
        try
        {
            batch = await _runtime.ExecuteAsync(
                session.SessionId,
                session.SnapshotPath,
                session.SnapshotId,
                session.SnapshotSha256,
                plan,
                session.PathMappings,
                session.CompletedActionIds.ToHashSet(StringComparer.Ordinal),
                retryIds,
                password,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            bool payloadDeleted = await _runtime.CleanupPayloadAsync(
                sessionId,
                CancellationToken.None).ConfigureAwait(false);
            RecoveryWizardSession cancelled = Touch(session with
            {
                Status = RecoveryWizardStatus.Cancelled,
                FailureReasonCode = payloadDeleted
                    ? "UserCancelled"
                    : "UserCancelledPayloadCleanupPending",
            });
            await _sessionStore.SaveAsync(cancelled, CancellationToken.None).ConfigureAwait(false);
            return cancelled;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            bool payloadDeleted = await _runtime.CleanupPayloadAsync(
                sessionId,
                CancellationToken.None).ConfigureAwait(false);
            RecoveryWizardSession failed = Touch(session with
            {
                Status = RecoveryWizardStatus.Failed,
                FailureReasonCode = payloadDeleted
                    ? "ExecutionFailed"
                    : "PayloadCleanupPending",
            });
            await _sessionStore.SaveAsync(failed, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        RecoveryWizardSession merged = MergeExecution(session, batch);
        RecoveryWizardSession updated = merged with
        {
            ReviewBindingSha256 = ComputeReviewBinding(merged, plan),
        };
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
            session.SnapshotId,
            session.SnapshotSha256,
            session.PathMappings,
            password,
            cancellationToken).ConfigureAwait(false);
        bool payloadDeleted = await _runtime.CleanupPayloadAsync(
            session.SessionId,
            CancellationToken.None).ConfigureAwait(false);
        RecoveryWizardSession completed = Touch(verifying with
        {
            Status = RecoveryWizardStatus.Completed,
            CurrentStep = RecoveryWizardStep.ShowManualActions,
            SimilarityAfter = verification.SimilarityAfter,
            ManualActions = session.ManualActions
                .Concat(verification.ManualActions)
                .DistinctBy(action => action.Id, StringComparer.Ordinal)
                .ToArray(),
            FailureReasonCode = payloadDeleted ? null : "PayloadCleanupPending",
        });
        await _sessionStore.SaveAsync(completed, CancellationToken.None).ConfigureAwait(false);
        await TryRecordRestoreHistoryAsync(completed, CancellationToken.None).ConfigureAwait(false);
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
            FailureReasonCode = batch.PayloadCleanupPending
                ? "PayloadCleanupPending"
                : batch.WasCancelled ? "UserCancelled" : null,
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

    private static void ValidateReviewBinding(
        RecoveryWizardSession session,
        RestorePlan plan,
        string reviewedBindingSha256)
    {
        string actual = ComputeReviewBinding(session, plan);
        if (session.ReviewBindingSha256 is null ||
            !FixedHashEquals(session.ReviewBindingSha256, actual) ||
            !FixedHashEquals(reviewedBindingSha256, actual))
        {
            throw new InvalidOperationException(
                "The recovery plan, snapshot, or destination mapping changed after review. Review it again.");
        }
    }

    private static string ComputeReviewBinding(
        RecoveryWizardSession session,
        RestorePlan plan)
    {
        byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = ReviewBindingVersion,
            session.SessionId,
            session.SnapshotId,
            SnapshotSha256 = session.SnapshotSha256.ToUpperInvariant(),
            Plan = plan,
            Mappings = session.PathMappings,
            CompletedActionIds = session.CompletedActionIds.Order(StringComparer.Ordinal),
            FailedActionIds = session.FailedActionIds.Order(StringComparer.Ordinal),
        });
        return Convert.ToHexString(SHA256.HashData(canonical));
    }

    private static bool FixedHashEquals(string first, string second)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(first),
                Convert.FromHexString(second));
        }
        catch (FormatException)
        {
            return false;
        }
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
                mapping.SourcePath.Any(char.IsControl) ||
                mapping.TargetPath.Any(char.IsControl) ||
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
