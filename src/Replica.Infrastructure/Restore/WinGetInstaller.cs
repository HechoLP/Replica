using Replica.Core.Execution;
using Replica.Core.Matching;
using Replica.Core.Scanning;
using Replica.Core.Services;

namespace Replica.Infrastructure.Restore;

public sealed class WinGetInstaller : IWinGetInstaller
{
    private const int RestartInitiatedExitCode = 1641;
    private const int RestartRequiredExitCode = 3010;
    private readonly IApplicationScanner _applicationScanner;
    private readonly IProcessRunner _processRunner;
    private readonly IApplicationVersionComparer _versionComparer;

    public WinGetInstaller(
        IProcessRunner processRunner,
        IApplicationScanner applicationScanner,
        IApplicationVersionComparer versionComparer)
    {
        _processRunner = processRunner;
        _applicationScanner = applicationScanner;
        _versionComparer = versionComparer;
    }

    public async Task<WinGetInstallResult> InstallAsync(
        WinGetInstallRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Confidence is ApplicationMatchConfidence.Low or ApplicationMatchConfidence.Unknown)
        {
            return Failed("LowConfidenceBlocked");
        }

        if (!_processRunner.IsToolAvailable(ProcessTool.WinGet))
        {
            return Failed("WinGetUnavailable");
        }

        ApplicationScanResult before = await _applicationScanner.ScanAsync(null, cancellationToken)
            .ConfigureAwait(false);
        ScannedApplication? installed = FindPackage(before.Applications, request.PackageIdentifier);
        if (installed is not null && !request.IsUpdate)
        {
            return new WinGetInstallResult(
                RestoreExecutionState.Skipped,
                "PackageAlreadyInstalled",
                0,
                string.Empty,
                string.Empty,
                false,
                false,
                false);
        }

        if (installed is not null && request.IsUpdate &&
            VersionMatches(request.ExpectedVersion, installed.Version))
        {
            return new WinGetInstallResult(
                RestoreExecutionState.Skipped,
                "PackageAlreadyAtTargetVersion",
                0,
                string.Empty,
                string.Empty,
                false,
                false,
                false);
        }

        ProcessExecutionResult process = await _processRunner.RunAsync(
            new ProcessRequest(
                ProcessOperation.WinGetInstall,
                request.Timeout,
                1_048_576,
                request.PackageIdentifier),
            cancellationToken).ConfigureAwait(false);
        if (process.TimedOut)
        {
            return FromProcess(process, RestoreExecutionState.Failed, "WinGetInstallTimeout", false);
        }

        bool requiresRestart = process.ExitCode is RestartInitiatedExitCode or RestartRequiredExitCode;
        if (process.ExitCode != 0 && !requiresRestart)
        {
            return FromProcess(process, RestoreExecutionState.Failed, "WinGetInstallFailed", false);
        }

        ApplicationScanResult after = await _applicationScanner.ScanAsync(null, cancellationToken)
            .ConfigureAwait(false);
        ScannedApplication? verified = FindPackage(after.Applications, request.PackageIdentifier);
        if (verified is null || !VersionMatches(request.ExpectedVersion, verified.Version))
        {
            return FromProcess(process, RestoreExecutionState.Failed, "PackageVerificationFailed", requiresRestart);
        }

        return FromProcess(
            process,
            requiresRestart ? RestoreExecutionState.RequiresRestart : RestoreExecutionState.Succeeded,
            requiresRestart ? "PackageInstalledRestartRequired" : "PackageInstalledAndVerified",
            requiresRestart);
    }

    private bool VersionMatches(string? expected, string? actual)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            return !string.IsNullOrWhiteSpace(actual);
        }

        return _versionComparer.Compare(expected, actual).Comparison ==
            ApplicationVersionComparison.Equal;
    }

    private static ScannedApplication? FindPackage(
        IEnumerable<ScannedApplication> applications,
        string packageIdentifier)
    {
        return applications.FirstOrDefault(application =>
            packageIdentifier.Equals(application.WinGetId, StringComparison.OrdinalIgnoreCase));
    }

    private static void ValidateRequest(WinGetInstallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.PackageIdentifier is not { Length: > 1 and <= 255 } identifier ||
            !identifier.Contains('.', StringComparison.Ordinal) ||
            identifier.Any(character =>
                !char.IsLetterOrDigit(character) && character is not ('.' or '-' or '_')) ||
            !Enum.IsDefined(request.Confidence) ||
            request.Timeout <= TimeSpan.Zero ||
            request.Timeout > TimeSpan.FromHours(1))
        {
            throw new ArgumentException("The winget installation request is invalid.", nameof(request));
        }
    }

    private static WinGetInstallResult Failed(string reasonCode)
    {
        return new WinGetInstallResult(
            RestoreExecutionState.Failed,
            reasonCode,
            null,
            string.Empty,
            string.Empty,
            false,
            false,
            false);
    }

    private static WinGetInstallResult FromProcess(
        ProcessExecutionResult process,
        RestoreExecutionState state,
        string reasonCode,
        bool requiresRestart)
    {
        return new WinGetInstallResult(
            state,
            reasonCode,
            process.ExitCode,
            process.StandardOutput,
            process.StandardError,
            process.TimedOut,
            process.OutputTruncated,
            requiresRestart);
    }
}
