using Replica.Core.Scanning;

namespace Replica.Core.Services;

public interface IFontScanner
{
    Task<FontScanResult> ScanAsync(CancellationToken cancellationToken);
}
