using Replica.Core.Platforms;
using Replica.Core.Services;
using Replica.Core.Snapshots;

namespace Replica.Mac.Infrastructure.Snapshots;

public interface IMacLightweightSnapshotService
{
    Task<ReplicaSnapshotManifest> CreateAsync(
        string destinationPath,
        string productVersion,
        PlatformScanResult scan,
        IProgress<ReplicaSnapshotProgress>? progress,
        CancellationToken cancellationToken);
}

public sealed class MacLightweightSnapshotService : IMacLightweightSnapshotService
{
    private readonly ISnapshotWriter writer;

    public MacLightweightSnapshotService(ISnapshotWriter writer)
    {
        this.writer = writer;
    }

    public Task<ReplicaSnapshotManifest> CreateAsync(
        string destinationPath,
        string productVersion,
        PlatformScanResult scan,
        IProgress<ReplicaSnapshotProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(productVersion);
        ArgumentNullException.ThrowIfNull(scan);

        ReplicaWindowsInfo compatibilityInfo = new(
            scan.Platform.DisplayName,
            scan.Platform.Version,
            scan.Platform.Build,
            scan.Platform.Architecture,
            scan.Platform.Locale,
            scan.Platform.TimeZone,
            scan.Platform.Capabilities);
        ReplicaSnapshotInventory inventory = new(
            scan.Applications.Select(MapApplication).ToArray(),
            new ReplicaEnvironmentInventory(
                scan.EnvironmentVariables
                    .Where(variable => !variable.IsSensitive && variable.Value is not null)
                    .Select(variable => new ReplicaEnvironmentVariable(variable.Name, variable.Value!, "Process"))
                    .ToArray(),
                scan.PathEntries.Select(entry => new ReplicaPathEntry(entry.Value, "Process", entry.Order)).ToArray()),
            compatibilityInfo,
            scan.Fonts.Select(font => new ReplicaFontInfo(font.FamilyName, font.Style, null, null)).ToArray(),
            []);
        IReadOnlyList<ReplicaExclusion> exclusions = scan.EnvironmentVariables
            .Where(variable => variable.IsSensitive)
            .Select(variable => new ReplicaExclusion(variable.Name, "SensitiveEnvironmentVariable", variable.ExclusionReason))
            .Concat(
            [
                new ReplicaExclusion("~/Library", "BroadLocationExcluded"),
                new ReplicaExclusion("/System", "OperatingSystemFilesExcluded"),
                new ReplicaExclusion("BrowserProfiles", "SensitiveProfileExcluded"),
                new ReplicaExclusion("Keychain", "CredentialsExcluded"),
            ])
            .OrderBy(exclusion => exclusion.Path, StringComparer.Ordinal)
            .ToArray();
        ReplicaMachineInfo machine = new(
            scan.MachineName,
            compatibilityInfo,
            scan.Platform.Architecture,
            scan.Platform.Locale,
            scan.Platform);
        ReplicaSnapshotWriteRequest request = new(
            Path.GetFullPath(destinationPath),
            SnapshotType.Lightweight,
            productVersion,
            machine,
            inventory,
            scan.Platform.Capabilities,
            exclusions,
            new ReplicaRecoveryOptions("RenameAndKeepBoth", 0, null, [], []));
        return writer.WriteAsync(request, progress, cancellationToken);
    }

    private static ReplicaApplication MapApplication(PlatformApplication application)
    {
        return new ReplicaApplication(
            application.Name,
            application.Version,
            application.Publisher,
            application.Architecture,
            "User",
            application.Source,
            new ReplicaPackageIdentity(
                null,
                null,
                null,
                application.BundleIdentifier,
                application.HomebrewPackageId),
            []);
    }
}

public sealed class EmptySnapshotSelectionEstimator : ISnapshotSelectionEstimator
{
    public Task<ReplicaSelectionEstimate> EstimateAsync(
        IReadOnlyList<ReplicaSelectedFolder> selectedFolders,
        IReadOnlyList<ReplicaOfflineInstaller> offlineInstallers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selectedFolders);
        ArgumentNullException.ThrowIfNull(offlineInstallers);
        cancellationToken.ThrowIfCancellationRequested();
        if (selectedFolders.Count != 0 || offlineInstallers.Count != 0)
        {
            throw new ReplicaSnapshotException("The macOS preview supports Lightweight snapshots only.");
        }

        return Task.FromResult(new ReplicaSelectionEstimate([], [], 0));
    }
}
