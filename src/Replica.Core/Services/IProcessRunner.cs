using Replica.Core.Scanning;

namespace Replica.Core.Services;

public interface IProcessRunner
{
    bool IsToolAvailable(ProcessTool tool);

    Task<ProcessExecutionResult> RunAsync(
        ProcessRequest request,
        CancellationToken cancellationToken);
}
