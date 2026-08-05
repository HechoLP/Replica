using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Replica.Core.Recovery;
using Replica.Core.Services;
using Replica.Infrastructure.Snapshots;

namespace Replica.Infrastructure.Recovery;

public sealed class JsonRecoverySessionStore : IRecoverySessionStore
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 64,
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IReplicaPathProvider _pathProvider;

    public JsonRecoverySessionStore(IReplicaPathProvider pathProvider)
    {
        _pathProvider = pathProvider;
    }

    public async Task SaveAsync(
        RecoveryWizardSession session,
        CancellationToken cancellationToken)
    {
        ValidateSessionId(session.SessionId);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(session, JsonOptions);
        RecoverySessionEnvelope envelope = new(
            SchemaVersion,
            Convert.ToBase64String(payload),
            Convert.ToHexString(SHA256.HashData(payload)));
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string path = GetSessionPath(session.SessionId, createDirectory: true);
            string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                await using (FileStream stream = new(
                                 temporary,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 16 * 1024,
                                 FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                TryDelete(temporary);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RecoveryWizardSession?> LoadAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        ValidateSessionId(sessionId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string path = GetSessionPath(sessionId, createDirectory: false);
            if (!File.Exists(path))
            {
                return null;
            }

            byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            RecoverySessionEnvelope envelope = JsonSerializer.Deserialize<RecoverySessionEnvelope>(
                bytes,
                JsonOptions) ?? throw new InvalidDataException("Recovery session state is invalid.");
            if (envelope.SchemaVersion != SchemaVersion)
            {
                throw new InvalidDataException("Recovery session schema is unsupported.");
            }

            byte[] payload = Convert.FromBase64String(envelope.Payload);
            byte[] expected = Convert.FromHexString(envelope.Sha256);
            byte[] actual = SHA256.HashData(payload);
            if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            {
                throw new InvalidDataException("Recovery session integrity validation failed.");
            }

            RecoveryWizardSession session = JsonSerializer.Deserialize<RecoveryWizardSession>(
                payload,
                JsonOptions) ?? throw new InvalidDataException("Recovery session state is invalid.");
            if (!session.SessionId.Equals(sessionId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Recovery session identity is inconsistent.");
            }

            return session;
        }
        catch (Exception exception) when (
            exception is JsonException or FormatException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("Recovery session state could not be validated.", exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string GetSessionPath(string sessionId, bool createDirectory)
    {
        string recoveryRoot = Path.GetFullPath(_pathProvider.RecoveryDirectory);
        string sessionsRoot = Path.Combine(recoveryRoot, "Sessions");
        string directory = Path.Combine(sessionsRoot, sessionId);
        string fullDirectory = Path.GetFullPath(directory);
        string prefix = sessionsRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullDirectory.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Recovery session path escaped its root.");
        }

        if (createDirectory)
        {
            Directory.CreateDirectory(fullDirectory);
        }
        else if (!Directory.Exists(fullDirectory))
        {
            return Path.Combine(fullDirectory, "session.json");
        }

        if (Directory.Exists(fullDirectory) && SnapshotPathValidator.ContainsReparsePoint(fullDirectory))
        {
            throw new InvalidDataException("Recovery session directory cannot be a reparse point.");
        }

        return Path.Combine(fullDirectory, "session.json");
    }

    private static void ValidateSessionId(string sessionId)
    {
        if (!Guid.TryParseExact(sessionId, "N", out _))
        {
            throw new ArgumentException("The recovery session identifier is invalid.", nameof(sessionId));
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record RecoverySessionEnvelope(
        int SchemaVersion,
        string Payload,
        string Sha256);
}
