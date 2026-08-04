using Replica.Core.Scanning;

namespace Replica.Core.Services;

public interface IEnvironmentVariableScanner
{
    Task<EnvironmentVariableScanResult> ScanAsync(CancellationToken cancellationToken);
}
