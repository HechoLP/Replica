using Replica.Core.Platforms;

namespace Replica.Core.Services;

public interface IPlatformEnvironmentScanner
{
    Task<PlatformScanResult> ScanAsync(
        IProgress<PlatformScanProgress>? progress,
        CancellationToken cancellationToken);
}
