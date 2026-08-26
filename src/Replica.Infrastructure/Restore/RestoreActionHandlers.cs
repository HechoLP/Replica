using Replica.Core.Execution;
using Replica.Core.Matching;
using Replica.Core.Planning;
using Replica.Core.Services;

namespace Replica.Infrastructure.Restore;

public sealed class PackageRestoreActionHandler : IRestoreActionHandler
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(15);
    private readonly IWinGetInstaller _installer;

    public PackageRestoreActionHandler(IWinGetInstaller installer)
    {
        _installer = installer;
    }

    public bool CanHandle(RestoreActionType actionType)
    {
        return actionType is RestoreActionType.InstallPackage or RestoreActionType.UpdatePackage;
    }

    public async Task<RestoreActionExecutionResult> ExecuteAsync(
        RestoreAction action,
        IRestoreExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (context.IsElevated)
        {
            return new RestoreActionExecutionResult(
                action.Id,
                RestoreExecutionState.Failed,
                "ElevatedPackageExecutionBlocked",
                "Package restore must run without Replica administrator privileges.");
        }

        ApplicationMatchConfidence confidence = action.MatchConfidence ?? ApplicationMatchConfidence.Unknown;
        WinGetInstallResult result = await _installer.InstallAsync(
            new WinGetInstallRequest(
                action.SourceDiffKey,
                action.TargetValue,
                confidence,
                action.Type == RestoreActionType.UpdatePackage,
                DefaultTimeout),
            cancellationToken).ConfigureAwait(false);
        return new RestoreActionExecutionResult(
            action.Id,
            result.State,
            result.ReasonCode,
            GetMessage(result.State),
            result.RequiresRestart,
            result.ExitCode,
            result.OutputTruncated);
    }

    private static string GetMessage(RestoreExecutionState state)
    {
        return state switch
        {
            RestoreExecutionState.Succeeded => "The package was installed and verified.",
            RestoreExecutionState.RequiresRestart => "The package was verified and requires a restart.",
            RestoreExecutionState.Skipped => "The package already satisfied the restore plan.",
            _ => "The package installation did not complete successfully.",
        };
    }
}

public sealed class EnvironmentRestoreActionHandler : IRestoreActionHandler
{
    private readonly IEnvironmentWriter _writer;

    public EnvironmentRestoreActionHandler(IEnvironmentWriter writer)
    {
        _writer = writer;
    }

    public bool CanHandle(RestoreActionType actionType)
    {
        return actionType is
            RestoreActionType.SetUserEnvironmentVariable or
            RestoreActionType.SetMachineEnvironmentVariable or
            RestoreActionType.AddPathEntry;
    }

    public async Task<RestoreActionExecutionResult> ExecuteAsync(
        RestoreAction action,
        IRestoreExecutionContext context,
        CancellationToken cancellationToken)
    {
        bool path = action.Type == RestoreActionType.AddPathEntry;
        EnvironmentVariableScope scope = action.Type == RestoreActionType.SetMachineEnvironmentVariable ||
            path && action.RequiresAdministrator
            ? EnvironmentVariableScope.Machine
            : EnvironmentVariableScope.User;
        string name = path
            ? "PATH"
            : RestoreActionKeyParser.GetEnvironmentVariableName(action, scope);
        EnvironmentWriteResult result = await _writer.WriteAsync(
            new EnvironmentWriteRequest(
                name,
                action.TargetValue,
                scope,
                path,
                context.IsElevated),
            cancellationToken).ConfigureAwait(false);
        return new RestoreActionExecutionResult(
            action.Id,
            result.State,
            result.ReasonCode,
            result.LengthWarning
                ? "The environment change completed with a PATH length warning."
                : "The environment change was processed.");
    }
}

public sealed class RegistryRestoreActionHandler : IRestoreActionHandler
{
    private readonly IRegistryWriter _writer;

    public RegistryRestoreActionHandler(IRegistryWriter writer)
    {
        _writer = writer;
    }

    public bool CanHandle(RestoreActionType actionType)
    {
        return actionType == RestoreActionType.RestoreRegistryValue;
    }

    public async Task<RestoreActionExecutionResult> ExecuteAsync(
        RestoreAction action,
        IRestoreExecutionContext context,
        CancellationToken cancellationToken)
    {
        RegistryWriteRequest request = ParseRequest(action, context.IsElevated);
        RegistryWriteResult result = await _writer.WriteAsync(request, cancellationToken)
            .ConfigureAwait(false);
        return new RestoreActionExecutionResult(
            action.Id,
            result.State,
            result.ReasonCode,
            "The allow-listed registry action was processed.");
    }

    private static RegistryWriteRequest ParseRequest(RestoreAction action, bool isElevated)
    {
        const string prefix = "registry:";
        if (!action.SourceDiffKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The registry action key is invalid.", nameof(action));
        }

        string[] fields = action.SourceDiffKey[prefix.Length..].Split('|');
        if (fields.Length != 3 ||
            !Enum.TryParse(fields[2], ignoreCase: true, out RegistryValueDataKind kind))
        {
            throw new ArgumentException("The registry action key is invalid.", nameof(action));
        }

        int separator = fields[0].IndexOf('\\');
        if (separator <= 0 || separator == fields[0].Length - 1)
        {
            throw new ArgumentException("The registry action path is invalid.", nameof(action));
        }

        return new RegistryWriteRequest(
            fields[0][..separator].ToUpperInvariant(),
            fields[0][(separator + 1)..],
            fields[1],
            kind,
            action.TargetValue,
            isElevated);
    }
}

public sealed class FileRestoreActionHandler : IRestoreActionHandler
{
    private readonly IFileRestoreService _fileRestoreService;

    public FileRestoreActionHandler(IFileRestoreService fileRestoreService)
    {
        _fileRestoreService = fileRestoreService;
    }

    public bool CanHandle(RestoreActionType actionType)
    {
        return actionType is RestoreActionType.RestoreFile or RestoreActionType.RestoreSelectedUserFile;
    }

    public async Task<RestoreActionExecutionResult> ExecuteAsync(
        RestoreAction action,
        IRestoreExecutionContext context,
        CancellationToken cancellationToken)
    {
        FileRestoreRequest request = context.GetFileRestoreRequest(action.Id) ??
            throw new InvalidOperationException("The approved file mapping is missing.");
        FileRestoreResult result = await _fileRestoreService.RestoreAsync(request, cancellationToken)
            .ConfigureAwait(false);
        return new RestoreActionExecutionResult(
            action.Id,
            result.State,
            result.ReasonCode,
            result.ConflictRequiresConfirmation
                ? "The file conflict requires user confirmation."
                : "The file restore action was processed.",
            MutationTargetPath: result.RestoredPath);
    }
}

public sealed class ValidationRestoreActionHandler : IRestoreActionHandler
{
    public bool CanHandle(RestoreActionType actionType)
    {
        return actionType == RestoreActionType.Validate;
    }

    public Task<RestoreActionExecutionResult> ExecuteAsync(
        RestoreAction action,
        IRestoreExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new RestoreActionExecutionResult(
            action.Id,
            RestoreExecutionState.Succeeded,
            "MutationHandlerVerified",
            "The preceding allow-listed handler completed its built-in verification."));
    }
}
