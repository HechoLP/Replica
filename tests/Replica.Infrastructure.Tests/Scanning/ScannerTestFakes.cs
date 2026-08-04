using Replica.Core.Scanning;
using Replica.Core.Services;
using Replica.Infrastructure.Scanning;

namespace Replica.Infrastructure.Tests.Scanning;

internal sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Dictionary<ProcessOperation, ProcessExecutionResult> _results = [];

    public bool WinGetAvailable { get; set; } = true;

    public bool PowerShellAvailable { get; set; } = true;

    public List<ProcessOperation> Operations { get; } = [];

    public void SetResult(ProcessOperation operation, ProcessExecutionResult result)
    {
        _results[operation] = result;
    }

    public bool IsToolAvailable(ProcessTool tool)
    {
        return tool switch
        {
            ProcessTool.WinGet => WinGetAvailable,
            ProcessTool.PowerShell => PowerShellAvailable,
            ProcessTool.WindowsTerminal => false,
            _ => false,
        };
    }

    public Task<ProcessExecutionResult> RunAsync(
        ProcessRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Operations.Add(request.Operation);
        return Task.FromResult(
            _results.GetValueOrDefault(
                request.Operation,
                new ProcessExecutionResult(0, string.Empty, string.Empty, false, false)));
    }
}

internal sealed class FakeRegistryApplicationSource(
    IReadOnlyList<RegistryApplicationRecord> records) : IRegistryApplicationSource
{
    public Task<IReadOnlyList<RegistryApplicationRecord>> ReadAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(records);
    }
}

internal sealed class FakeEnvironmentValueSource : IEnvironmentValueSource
{
    public IReadOnlyDictionary<string, string> MachineValues { get; init; } =
        new Dictionary<string, string>();

    public IReadOnlyDictionary<string, string> UserValues { get; init; } =
        new Dictionary<string, string>();

    public Task<IReadOnlyDictionary<string, string>> ReadAsync(
        EnvironmentVariableTarget target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            target == EnvironmentVariableTarget.Machine ? MachineValues : UserValues);
    }
}
