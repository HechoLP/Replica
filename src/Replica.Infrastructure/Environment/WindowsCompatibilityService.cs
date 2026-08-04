using Replica.Core.Models;
using Replica.Core.Services;

namespace Replica.Infrastructure.Environment;

public sealed class WindowsCompatibilityService : IWindowsCompatibilityService
{
    private static readonly Version MinimumWindows11Version = new(10, 0, 22000);
    private readonly IOperatingSystemInfo _operatingSystem;

    public WindowsCompatibilityService(IOperatingSystemInfo operatingSystem)
    {
        _operatingSystem = operatingSystem;
    }

    public WindowsCompatibilityInfo GetCompatibility()
    {
        bool isSupported = _operatingSystem.IsWindows
            && _operatingSystem.Version >= MinimumWindows11Version;

        string message = isSupported
            ? "Windows 11 compatibility confirmed."
            : "Replica requires Windows 11 build 22000 or later.";

        return new WindowsCompatibilityInfo(
            isSupported,
            _operatingSystem.Version,
            _operatingSystem.Description,
            message);
    }
}
