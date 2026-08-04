using Replica.Core.Scanning;

namespace Replica.Core.Services;

public interface IApplicationScanner
{
    Task<ApplicationScanResult> ScanAsync(
        IProgress<EnvironmentScanProgress>? progress,
        CancellationToken cancellationToken);
}
