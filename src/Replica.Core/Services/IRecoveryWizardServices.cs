using Replica.Core.Planning;
using Replica.Core.Recovery;
using Replica.Core.Snapshots;

namespace Replica.Core.Services;

public interface IRecoveryWizardService
{
    Task<RecoveryStartResult> StartAsync(
        string snapshotPath,
        ReadOnlyMemory<char> password,
        CancellationToken cancellationToken);

    Task<RecoveryWizardSession> AnalyzeAsync(
        string sessionId,
        IReadOnlyList<RecoveryPathMapping> mappings,
        ReadOnlyMemory<char> password,
        CancellationToken cancellationToken);

    Task<RecoveryWizardSession> ApprovePlanAsync(
        string sessionId,
        string reviewedBindingSha256,
        CancellationToken cancellationToken);

    Task<RecoveryWizardSession> ExecuteAsync(
        string sessionId,
        string reviewedBindingSha256,
        ReadOnlyMemory<char> password,
        CancellationToken cancellationToken);

    Task<RecoveryWizardSession> RetryFailedAsync(
        string sessionId,
        string reviewedBindingSha256,
        ReadOnlyMemory<char> password,
        CancellationToken cancellationToken);

    Task<RecoveryWizardSession> ApproveRestartAsync(
        string sessionId,
        bool userApproved,
        CancellationToken cancellationToken);

    Task<RecoveryWizardSession?> LoadForResumeAsync(
        string sessionId,
        CancellationToken cancellationToken);

    Task<RecoveryWizardSession> ConfirmResumeAsync(
        string sessionId,
        ReadOnlyMemory<char> password,
        CancellationToken cancellationToken);

    Task<RecoveryWizardSession> CancelAsync(
        string sessionId,
        CancellationToken cancellationToken);

    Task<OfflineInstallerExportResult> ExportOfflineInstallersAsync(
        string sessionId,
        string destinationDirectory,
        ReadOnlyMemory<char> password,
        CancellationToken cancellationToken);
}

public interface IRecoveryWizardRuntime
{
    Task<RecoveryPreparedSnapshot> OpenSnapshotAsync(
        string snapshotPath,
        ReadOnlyMemory<char> password,
        CancellationToken cancellationToken);

    Task<RecoveryAnalysisResult> AnalyzeAsync(
        string snapshotPath,
        IReadOnlyList<RecoveryPathMapping> mappings,
        ReadOnlyMemory<char> password,
        CancellationToken cancellationToken);

    Task<RecoveryExecutionBatch> ExecuteAsync(
        string sessionId,
        string snapshotPath,
        Guid expectedSnapshotId,
        string expectedSnapshotSha256,
        RestorePlan approvedPlan,
        IReadOnlyList<RecoveryPathMapping> mappings,
        IReadOnlySet<string> completedActionIds,
        IReadOnlySet<string>? retryActionIds,
        ReadOnlyMemory<char> password,
        CancellationToken cancellationToken);

    Task<RecoveryVerificationResult> VerifyAsync(
        string snapshotPath,
        Guid expectedSnapshotId,
        string expectedSnapshotSha256,
        IReadOnlyList<RecoveryPathMapping> mappings,
        ReadOnlyMemory<char> password,
        CancellationToken cancellationToken);

    Task<bool> CleanupPayloadAsync(
        string sessionId,
        CancellationToken cancellationToken);
}

public interface IRecoverySessionStore
{
    Task SaveAsync(RecoveryWizardSession session, CancellationToken cancellationToken);

    Task<RecoveryWizardSession?> LoadAsync(string sessionId, CancellationToken cancellationToken);
}

public interface IRecoveryStartupRegistrar
{
    Task RegisterAsync(
        string sessionId,
        bool userApproved,
        CancellationToken cancellationToken);

    Task UnregisterAsync(string sessionId, CancellationToken cancellationToken);
}

public interface IRecoveryHardwareScanner
{
    Task<ReplicaHardwareInfo> ScanAsync(CancellationToken cancellationToken);
}
