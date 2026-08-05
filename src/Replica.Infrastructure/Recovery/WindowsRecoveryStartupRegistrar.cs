using Replica.Core.Diffing;
using Replica.Core.Execution;
using Replica.Core.Planning;
using Replica.Core.Rollback;
using Replica.Core.Services;
using Replica.Infrastructure.Restore;

namespace Replica.Infrastructure.Recovery;

public sealed class WindowsRecoveryStartupRegistrar : IRecoveryStartupRegistrar
{
    public const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    public const string ValueName = "ReplicaRecoveryResume";
    private readonly IRestoreJournal _journal;
    private readonly IRegistryValueStore _registryStore;
    private readonly IRegistryWriter _registryWriter;

    public WindowsRecoveryStartupRegistrar(
        IRestoreJournal journal,
        IRegistryValueStore registryStore,
        IRegistryWriter registryWriter)
    {
        _journal = journal;
        _registryStore = registryStore;
        _registryWriter = registryWriter;
    }

    public async Task RegisterAsync(
        string sessionId,
        bool userApproved,
        CancellationToken cancellationToken)
    {
        ValidateSessionId(sessionId);
        if (!userApproved)
        {
            throw new InvalidOperationException("Automatic recovery resume requires explicit approval.");
        }

        string executable = System.Environment.ProcessPath ?? throw new InvalidOperationException(
            "Replica executable path is unavailable.");
        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable) ||
            !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Replica executable path is invalid.");
        }

        string command = $"\"{executable}\" --resume-recovery {sessionId}";
        RestoreAction action = CreateAction(
            sessionId,
            "register",
            command,
            "Register recovery resume after login.");
        await RecordAsync(sessionId, action, cancellationToken).ConfigureAwait(false);
        RegistryWriteResult result = await _registryWriter.WriteAsync(
            CreateRequest(command),
            cancellationToken).ConfigureAwait(false);
        if (result.State is not (RestoreExecutionState.Succeeded or RestoreExecutionState.Skipped))
        {
            throw new IOException("Recovery resume registration failed.");
        }

        await _journal.MarkActionStateAsync(
            sessionId,
            action.Id,
            RollbackJournalState.Applied,
            null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task UnregisterAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        ValidateSessionId(sessionId);
        RegistryWriteRequest request = CreateRequest(string.Empty);
        if (_registryStore.GetValue(request) is null)
        {
            return;
        }

        RestoreAction action = CreateAction(
            sessionId,
            "unregister",
            string.Empty,
            "Remove recovery resume registration.");
        await RecordAsync(sessionId, action, cancellationToken).ConfigureAwait(false);
        _registryStore.DeleteValue(request);
        await _journal.MarkActionStateAsync(
            sessionId,
            action.Id,
            RollbackJournalState.Applied,
            null,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task RecordAsync(
        string sessionId,
        RestoreAction action,
        CancellationToken cancellationToken)
    {
        await _journal.RecordBeforeMutationAsync(
            new RestoreJournalEntry(
                sessionId,
                action.Id,
                action.Type,
                DateTimeOffset.UtcNow,
                null,
                "RecoveryResumeRegistration",
                action),
            cancellationToken).ConfigureAwait(false);
    }

    private static RestoreAction CreateAction(
        string sessionId,
        string suffix,
        string targetValue,
        string description)
    {
        return new RestoreAction(
            $"recovery-resume-{suffix}",
            RestoreActionType.RestoreRegistryValue,
            "Replica recovery resume",
            description,
            null,
            null,
            targetValue,
            DiffRiskLevel.Medium,
            false,
            false,
            true,
            [],
            TimeSpan.FromSeconds(1),
            0,
            true,
            false,
            DiffArea.PluginSettings,
            $"registry:HKCU\\{RunKeyPath}|{ValueName}|String",
            $"RecoveryResume:{sessionId}");
    }

    private static RegistryWriteRequest CreateRequest(string value)
    {
        return new RegistryWriteRequest(
            "HKCU",
            RunKeyPath,
            ValueName,
            RegistryValueDataKind.String,
            value,
            false);
    }

    private static void ValidateSessionId(string sessionId)
    {
        if (!Guid.TryParseExact(sessionId, "N", out _))
        {
            throw new ArgumentException("The recovery session identifier is invalid.", nameof(sessionId));
        }
    }
}
