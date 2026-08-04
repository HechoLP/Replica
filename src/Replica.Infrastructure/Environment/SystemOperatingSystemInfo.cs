using System.Runtime.InteropServices;
using Replica.Core.Services;

namespace Replica.Infrastructure.Environment;

public sealed class SystemOperatingSystemInfo : IOperatingSystemInfo
{
    public bool IsWindows => OperatingSystem.IsWindows();

    public Version Version => System.Environment.OSVersion.Version;

    public string Description => RuntimeInformation.OSDescription;
}
