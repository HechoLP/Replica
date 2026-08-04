using Replica.Core.Scanning;

namespace Replica.Core.Services;

public interface IWinGetScanner
{
    Task<WinGetScanResult> ScanAsync(CancellationToken cancellationToken);
}
