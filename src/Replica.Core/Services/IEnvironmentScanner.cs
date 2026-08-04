using Replica.Core.Scanning;

namespace Replica.Core.Services;

public interface IEnvironmentScanner
{
    Task<EnvironmentScanResult> ScanAsync(
        IProgress<EnvironmentScanProgress>? progress,
        CancellationToken cancellationToken);
}
