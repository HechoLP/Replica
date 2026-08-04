using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Replica.Core.Execution;
using Replica.Core.Services;

namespace Replica.Infrastructure.Restore;

public sealed class FileRestoreJournal : IRestoreJournal
{
    private readonly IReplicaPathProvider _pathProvider;

    public FileRestoreJournal(IReplicaPathProvider pathProvider)
    {
        _pathProvider = pathProvider;
    }

    public async Task RecordBeforeMutationAsync(
        RestoreJournalEntry entry,
        CancellationToken cancellationToken)
    {
        ValidateEntry(entry);
        string sessionDirectory = GetSessionDirectory(entry.SessionId);
        Directory.CreateDirectory(sessionDirectory);
        RejectReparsePoint(sessionDirectory);

        string entryHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(entry.ActionId)))[..16];
        string destinationPath = Path.Combine(sessionDirectory, $"{entryHash}.json");
        string temporaryPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(entry);
        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(json, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            RejectReparsePoint(sessionDirectory);
            File.Move(temporaryPath, destinationPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private string GetSessionDirectory(string sessionId)
    {
        string rollbackRoot = Path.GetFullPath(_pathProvider.RollbackDirectory);
        string sessionDirectory = Path.GetFullPath(Path.Combine(rollbackRoot, sessionId));
        string prefix = rollbackRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!sessionDirectory.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The rollback session path is unsafe.");
        }

        return sessionDirectory;
    }

    private static void ValidateEntry(RestoreJournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.SessionId) ||
            entry.SessionId.Length > 128 ||
            entry.SessionId.Any(character =>
                !char.IsLetterOrDigit(character) && character is not ('-' or '_')) ||
            string.IsNullOrWhiteSpace(entry.ActionId) ||
            !Enum.IsDefined(entry.ActionType) ||
            string.IsNullOrWhiteSpace(entry.ReasonCode))
        {
            throw new ArgumentException("The restore journal entry is invalid.", nameof(entry));
        }
    }

    private static void RejectReparsePoint(string path)
    {
        string? current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current) && Directory.Exists(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("The rollback journal path contains a reparse point.");
            }

            current = Path.GetDirectoryName(current);
        }
    }
}
