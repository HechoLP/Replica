namespace Replica.Core.Scanning;

public enum ProcessOperation
{
    WinGetExport,
    WinGetList,
    MsixInventory,
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
    int MaximumOutputCharacters = 1_048_576);

public sealed record ProcessExecutionResult(
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    bool OutputTruncated);
