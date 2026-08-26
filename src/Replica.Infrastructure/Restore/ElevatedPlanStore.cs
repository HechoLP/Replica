using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Replica.Core.Execution;
using Replica.Core.Planning;
using Replica.Core.Services;

namespace Replica.Infrastructure.Restore;

public sealed class ElevatedPlanStore : IElevatedPlanStore
{
    private const int MaximumPlanBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan PlanLifetime = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        MaxDepth = 64,
        PropertyNameCaseInsensitive = false,
    };

    private readonly IReplicaPathProvider _pathProvider;
    private readonly TimeProvider _timeProvider;

    public ElevatedPlanStore(IReplicaPathProvider pathProvider, TimeProvider timeProvider)
    {
        _pathProvider = pathProvider;
        _timeProvider = timeProvider;
    }

    public async Task<ElevatedPlanCreationResult> CreateAsync(
        RestorePlan approvedPlan,
        RestoreAction administratorAction,
        IRestoreExecutionContext context,
        CancellationToken cancellationToken)
    {
        ValidateAdministratorAction(approvedPlan, administratorAction, context);
        string directory = GetPlanDirectory();
        EnsureRestrictedDirectory(directory);
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        string tokenHash = Sha256Hex(Encoding.UTF8.GetBytes(token));
        DateTimeOffset expires = _timeProvider.GetUtcNow().Add(PlanLifetime);
        RestoreAction elevatedAction = administratorAction with { Dependencies = [] };
        RestorePlan elevatedPlan = approvedPlan with
        {
            Actions = [elevatedAction],
            DryRun = approvedPlan.DryRun with { SelectedActionCount = 1 },
        };
        FileRestoreRequest? fileRequest = context.GetFileRestoreRequest(administratorAction.Id);
        ElevatedRestorePlanEnvelope envelope = new(
            ElevatedRestorePlanEnvelope.CurrentSchemaVersion,
            context.SessionId,
            expires,
            tokenHash,
            elevatedPlan,
            fileRequest is null ? [] : [fileRequest]);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        if (json.Length > MaximumPlanBytes)
        {
            throw new InvalidOperationException("The elevated restore plan exceeds its size limit.");
        }

        string planPath = Path.Combine(directory, $"elevated-{Guid.NewGuid():N}.json");
        await WriteRestrictedFileAsync(planPath, json, cancellationToken).ConfigureAwait(false);
        string digest = Sha256Hex(json);
        return new ElevatedPlanCreationResult(
            new ElevatedPlanLaunchRequest(planPath, digest, token, context.SessionId),
            expires);
    }

    public async Task<ElevatedRestorePlanEnvelope> ConsumeAsync(
        ElevatedExecutorArguments arguments,
        CancellationToken cancellationToken)
    {
        ValidateArguments(arguments);
        string planPath = GetContainedPlanPath(arguments.PlanFilePath);
        try
        {
            FileInfo info = new(planPath);
            if (!info.Exists ||
                info.Length is <= 0 or > MaximumPlanBytes ||
                (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("The elevated restore plan file is invalid.");
            }

            byte[] json = await File.ReadAllBytesAsync(planPath, cancellationToken)
                .ConfigureAwait(false);
            if (!FixedTimeHexEquals(Sha256Hex(json), arguments.PlanSha256))
            {
                throw new InvalidOperationException("The elevated restore plan integrity check failed.");
            }

            ElevatedRestorePlanEnvelope envelope = JsonSerializer.Deserialize<ElevatedRestorePlanEnvelope>(
                json,
                JsonOptions) ?? throw new InvalidOperationException("The elevated restore plan is invalid.");
            ValidateEnvelope(envelope, arguments);
            ConsumeToken(envelope, arguments.SingleUseToken);
            return envelope;
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("The elevated restore plan is invalid.");
        }
        finally
        {
            TryDeletePlan(planPath);
        }
    }

    public Task DiscardAsync(
        ElevatedPlanLaunchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        TryDeletePlan(GetContainedPlanPath(request.PlanFilePath));
        return Task.CompletedTask;
    }

    private void ValidateEnvelope(
        ElevatedRestorePlanEnvelope envelope,
        ElevatedExecutorArguments arguments)
    {
        if (envelope.SchemaVersion != ElevatedRestorePlanEnvelope.CurrentSchemaVersion ||
            !envelope.SessionId.Equals(arguments.SessionId, StringComparison.Ordinal) ||
            envelope.ExpiresAtUtc <= _timeProvider.GetUtcNow() ||
            envelope.ExpiresAtUtc > _timeProvider.GetUtcNow().Add(PlanLifetime) ||
            !FixedTimeHexEquals(
                envelope.TokenSha256,
                Sha256Hex(Encoding.UTF8.GetBytes(arguments.SingleUseToken))) ||
            !envelope.Plan.IsApproved ||
            envelope.Plan.Actions is not { Count: 1 } ||
            envelope.FileRequests is not { Count: 0 })
        {
            throw new InvalidOperationException("The elevated restore plan authorization is invalid or expired.");
        }

        RestoreAction action = envelope.Plan.Actions[0];
        if (!action.IsSelected ||
            !action.RequiresAdministrator ||
            action.IsManualOnly ||
            action.Dependencies.Count != 0 ||
            !IsAllowedElevatedAction(action.Type) ||
            envelope.FileRequests.Any(request => request is null))
        {
            throw new InvalidOperationException("The elevated restore plan contains a disallowed action.");
        }
    }

    private void ConsumeToken(ElevatedRestorePlanEnvelope envelope, string token)
    {
        string usedDirectory = Path.Combine(GetPlanDirectory(), "UsedTokens");
        EnsureRestrictedDirectory(usedDirectory);
        string markerName = Sha256Hex(
            Encoding.UTF8.GetBytes($"{envelope.SessionId}\u001F{token}"));
        string markerPath = Path.Combine(usedDirectory, $"{markerName}.used");
        using FileStream marker = new(
            markerPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            1,
            FileOptions.WriteThrough);
        marker.WriteByte(1);
        marker.Flush(flushToDisk: true);
        ApplyRestrictedFileAcl(markerPath);
    }

    private static void ValidateAdministratorAction(
        RestorePlan plan,
        RestoreAction action,
        IRestoreExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(context);
        if (!plan.IsApproved ||
            !action.IsSelected ||
            !action.RequiresAdministrator ||
            action.IsManualOnly ||
            !IsAllowedElevatedAction(action.Type) ||
            !plan.Actions.Any(candidate => candidate == action))
        {
            throw new InvalidOperationException("Only an approved administrator action can be elevated.");
        }
    }

    private static bool IsAllowedElevatedAction(RestoreActionType type)
    {
        return type is
            RestoreActionType.SetMachineEnvironmentVariable or
            RestoreActionType.AddPathEntry or
            RestoreActionType.RestoreRegistryValue;
    }

    private static void ValidateArguments(ElevatedExecutorArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (string.IsNullOrWhiteSpace(arguments.PlanFilePath) ||
            arguments.PlanSha256 is not { Length: 64 } digest ||
            !digest.All(Uri.IsHexDigit) ||
            arguments.SingleUseToken is not { Length: 64 } token ||
            !token.All(Uri.IsHexDigit) ||
            string.IsNullOrWhiteSpace(arguments.SessionId) ||
            arguments.SessionId.Length > 128)
        {
            throw new InvalidOperationException("The elevated executor arguments are invalid.");
        }
    }

    private string GetPlanDirectory()
    {
        return Path.GetFullPath(Path.Combine(_pathProvider.TemporaryDirectory, "ElevatedPlans"));
    }

    private string GetContainedPlanPath(string path)
    {
        string directory = GetPlanDirectory();
        string fullPath = Path.GetFullPath(path);
        string prefix = directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetExtension(fullPath).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The elevated restore plan path is invalid.");
        }

        return fullPath;
    }

    private static async Task WriteRestrictedFileAsync(
        string path,
        byte[] content,
        CancellationToken cancellationToken)
    {
        await using (FileStream stream = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        ApplyRestrictedFileAcl(path);
    }

    private static void EnsureRestrictedDirectory(string path)
    {
        Directory.CreateDirectory(path);
        DirectoryInfo directory = new(path);
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The elevated plan directory is unsafe.");
        }

        SecurityIdentifier user = WindowsIdentity.GetCurrent().User ??
            throw new InvalidOperationException("The current Windows identity is unavailable.");
        DirectorySecurity security = new();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddDirectoryRule(security, user);
        AddDirectoryRule(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        AddDirectoryRule(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        directory.SetAccessControl(security);
    }

    private static void AddDirectoryRule(DirectorySecurity security, SecurityIdentifier identity)
    {
        security.AddAccessRule(new FileSystemAccessRule(
            identity,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
    }

    private static void ApplyRestrictedFileAcl(string path)
    {
        SecurityIdentifier user = WindowsIdentity.GetCurrent().User ??
            throw new InvalidOperationException("The current Windows identity is unavailable.");
        FileSecurity security = new();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddFileRule(security, user);
        AddFileRule(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        AddFileRule(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        new FileInfo(path).SetAccessControl(security);
    }

    private static void AddFileRule(FileSecurity security, SecurityIdentifier identity)
    {
        security.AddAccessRule(new FileSystemAccessRule(
            identity,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
    }

    private static bool FixedTimeHexEquals(string first, string second)
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

    private static string Sha256Hex(byte[] value)
    {
        return Convert.ToHexString(SHA256.HashData(value));
    }

    private static void TryDeletePlan(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Startup cleanup retries deletion without exposing plan contents.
        }
        catch (UnauthorizedAccessException)
        {
            // Startup cleanup retries deletion without exposing plan contents.
        }
    }
}
