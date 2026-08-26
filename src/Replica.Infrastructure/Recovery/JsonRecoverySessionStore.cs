using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using Replica.Core.Recovery;
using Replica.Core.Services;
using Replica.Infrastructure.Snapshots;

namespace Replica.Infrastructure.Recovery;

public sealed class JsonRecoverySessionStore : IRecoverySessionStore
{
    private const int SchemaVersion = 1;
    internal const int MaximumSessionFileBytes = 4 * 1024 * 1024;
    internal const int MaximumSessionPayloadBytes = 2 * 1024 * 1024;
    private const int MaximumSessionItems = 1000;
    private const int MaximumPersistedStringLength = 32 * 1024;
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
        if (payload.Length > MaximumSessionPayloadBytes)
        {
            throw new InvalidDataException("Recovery session state exceeds its size limit.");
        }

        RecoverySessionEnvelope envelope = new(
            SchemaVersion,
            Convert.ToBase64String(payload),
            Convert.ToHexString(SHA256.HashData(payload)));
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        if (bytes.Length > MaximumSessionFileBytes)
        {
            throw new InvalidDataException("Recovery session envelope exceeds its size limit.");
        }

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

            FileInfo info = new(path);
            if (info.Length is <= 0 or > MaximumSessionFileBytes ||
                info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException("Recovery session file is invalid or exceeds its size limit.");
            }

            byte[] bytes = GC.AllocateUninitializedArray<byte>(checked((int)info.Length));
            await using (FileStream stream = new(
                             path,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             16 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                if (stream.ReadByte() != -1)
                {
                    throw new InvalidDataException("Recovery session changed while it was being read.");
                }
            }

            RecoverySessionEnvelope envelope = JsonSerializer.Deserialize<RecoverySessionEnvelope>(
                bytes,
                JsonOptions) ?? throw new InvalidDataException("Recovery session state is invalid.");
            int maximumBase64Characters = checked(((MaximumSessionPayloadBytes + 2) / 3) * 4);
            if (envelope.SchemaVersion != SchemaVersion ||
                envelope.Payload is not { Length: > 0 } encodedPayload ||
                encodedPayload.Length > maximumBase64Characters ||
                envelope.Sha256 is not { Length: 64 } digest ||
                !digest.All(Uri.IsHexDigit))
            {
                throw new InvalidDataException("Recovery session envelope is invalid or unsupported.");
            }

            byte[] payload = Convert.FromBase64String(encodedPayload);
            if (payload.Length > MaximumSessionPayloadBytes)
            {
                throw new InvalidDataException("Recovery session payload exceeds its size limit.");
            }

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

            ValidateSessionModel(session);

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

        if (createDirectory)
        {
            ApplyRestrictedDirectoryAcl(fullDirectory);
        }

        return Path.Combine(fullDirectory, "session.json");
    }

    private static void ApplyRestrictedDirectoryAcl(string path)
    {
        SecurityIdentifier user = WindowsIdentity.GetCurrent().User ??
            throw new InvalidOperationException("The current Windows identity is unavailable.");
        DirectorySecurity security = new();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddDirectoryRule(security, user);
        AddDirectoryRule(
            security,
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        AddDirectoryRule(
            security,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    private static void AddDirectoryRule(
        DirectorySecurity security,
        SecurityIdentifier identity)
    {
        security.AddAccessRule(new FileSystemAccessRule(
            identity,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
    }

    private static void ValidateSessionId(string sessionId)
    {
        if (!Guid.TryParseExact(sessionId, "N", out _))
        {
            throw new ArgumentException("The recovery session identifier is invalid.", nameof(sessionId));
        }
    }

    private static void ValidateSessionModel(RecoveryWizardSession session)
    {
        bool invalid = string.IsNullOrWhiteSpace(session.SnapshotPath) ||
            session.SnapshotPath.Length > MaximumPersistedStringLength ||
            session.SourceMachineName is null or { Length: > 512 } ||
            session.SourceWindowsVersion is null or { Length: > 512 } ||
            session.SourceArchitecture is null or { Length: > 128 } ||
            session.SourceLocale is null or { Length: > 128 } ||
            session.FailureReasonCode?.Length > 256 ||
            session.PathMappings is null or { Count: > MaximumSessionItems } ||
            session.HardwareDifferences is null or { Count: > MaximumSessionItems } ||
            session.ManualActions is null or { Count: > MaximumSessionItems } ||
            session.ActionResults is null or { Count: > MaximumSessionItems } ||
            session.CompletedActionIds is null or { Count: > MaximumSessionItems } ||
            session.FailedActionIds is null or { Count: > MaximumSessionItems } ||
            session.Plan?.Actions.Count > MaximumSessionItems;
        if (invalid)
        {
            throw new InvalidDataException("Recovery session state exceeds its model limits.");
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
            // Best-effort cleanup; an incomplete sibling file is never accepted as session state.
        }
        catch (UnauthorizedAccessException)
        {
            // Cleanup failure does not bypass the session envelope checksum on the next load.
        }
    }

    private sealed record RecoverySessionEnvelope(
        int SchemaVersion,
        string Payload,
        string Sha256);
}
