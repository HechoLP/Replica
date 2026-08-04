namespace Replica.Core.Models;

public sealed record WindowsCompatibilityInfo(
    bool IsSupported,
    Version OperatingSystemVersion,
    string Description,
    string Message);
