using Replica.Core.Platforms;
using Replica.Core.Snapshots;

namespace Replica.Mac.Infrastructure.Scanning;

public interface IMacSystemInfoSource
{
    Task<(ReplicaPlatformInfo Platform, string MachineName)> ReadAsync(
        CancellationToken cancellationToken);
}

public interface IMacApplicationSource
{
    Task<IReadOnlyList<PlatformApplication>> ReadAsync(CancellationToken cancellationToken);
}

public interface IHomebrewInventorySource
{
    Task<HomebrewInventoryResult> ReadAsync(CancellationToken cancellationToken);
}

public interface IMacEnvironmentSource
{
    Task<MacEnvironmentResult> ReadAsync(CancellationToken cancellationToken);
}

public interface IMacFontSource
{
    Task<IReadOnlyList<PlatformFont>> ReadAsync(CancellationToken cancellationToken);
}

public sealed record HomebrewInventoryResult(
    IReadOnlyList<PlatformApplication> Applications,
    IReadOnlyList<PlatformScanWarning> Warnings);

public sealed record MacEnvironmentResult(
    IReadOnlyList<PlatformEnvironmentVariable> Variables,
    IReadOnlyList<PlatformPathEntry> PathEntries);
