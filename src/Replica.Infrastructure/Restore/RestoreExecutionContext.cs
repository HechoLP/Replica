using Replica.Core.Execution;
using Replica.Core.Services;

namespace Replica.Infrastructure.Restore;

public sealed class RestoreExecutionContext : IRestoreExecutionContext
{
    private readonly IReadOnlyDictionary<string, FileRestoreRequest> _fileRequests;

    public RestoreExecutionContext(
        string sessionId,
        bool isElevated,
        IRestoreJournal journal,
        IEnumerable<KeyValuePair<string, FileRestoreRequest>>? fileRequests = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        SessionId = sessionId;
        IsElevated = isElevated;
        Journal = journal;
        _fileRequests = (fileRequests ?? [])
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    public string SessionId { get; }

    public bool IsElevated { get; }

    public IRestoreJournal Journal { get; }

    public FileRestoreRequest? GetFileRestoreRequest(string actionId)
    {
        return _fileRequests.GetValueOrDefault(actionId);
    }
}

public sealed class NullRestoreProgressReporter : IRestoreProgressReporter
{
    public Task ReportAsync(RestoreProgress progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
