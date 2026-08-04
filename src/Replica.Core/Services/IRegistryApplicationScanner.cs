using Replica.Core.Scanning;

namespace Replica.Core.Services;

public interface IRegistryApplicationScanner
{
    Task<RegistryApplicationScanResult> ScanAsync(CancellationToken cancellationToken);
}
