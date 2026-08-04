namespace Replica.Core.Services;

public interface IOperatingSystemInfo
{
    bool IsWindows { get; }

    Version Version { get; }

    string Description { get; }
}
