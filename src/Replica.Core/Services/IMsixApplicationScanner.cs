using Replica.Core.Scanning;

namespace Replica.Core.Services;

public interface IMsixApplicationScanner
{
    Task<MsixApplicationScanResult> ScanAsync(CancellationToken cancellationToken);
}
