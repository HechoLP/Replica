namespace Replica.Core.Scanning;

public enum ProcessOperation
{
    WinGetExport,
    WinGetList,
    WinGetInstall,
    MsixInventory,
    HardwareInventory,
}

public enum ProcessTool
{
    WinGet,
    PowerShell,
    WindowsTerminal,
}

public sealed record ProcessRequest(
    ProcessOperation Operation,
    TimeSpan Timeout,
    int MaximumOutputCharacters = 1_048_576,
    string? PackageIdentifier = null);

public sealed record ProcessExecutionResult(
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    bool OutputTruncated);
