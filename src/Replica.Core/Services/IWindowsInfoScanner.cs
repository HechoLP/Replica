using Replica.Core.Scanning;

namespace Replica.Core.Services;

public interface IWindowsInfoScanner
{
    Task<WindowsEnvironmentInfo> ScanAsync(CancellationToken cancellationToken);
}
