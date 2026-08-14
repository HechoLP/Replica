using System.Security.Cryptography;
using System.Text;
using Replica.Core.Diffing;
using Replica.Core.Matching;
using Replica.Core.Plugins;
using Replica.Core.Scanning;
using Replica.Core.Services;
using Replica.Core.Snapshots;

namespace Replica.Infrastructure.Snapshots;

public sealed class SnapshotComparisonService : ISnapshotComparisonService
{
    private readonly IEnvironmentDiffEngine _diffEngine;
    private readonly IEnvironmentScanner _environmentScanner;
    private readonly ISnapshotReader _snapshotReader;

    public SnapshotComparisonService(
        ISnapshotReader snapshotReader,
        IEnvironmentScanner environmentScanner,
        IEnvironmentDiffEngine diffEngine)
    {
        _snapshotReader = snapshotReader;
        _environmentScanner = environmentScanner;
        _diffEngine = diffEngine;
    }

    public async Task<SnapshotEnvironmentComparisonResult> CompareAsync(
        string snapshotPath,
        ReadOnlyMemory<char> password,
        DiffRestoreMode mode,
        CancellationToken cancellationToken)
    {
        ReplicaSnapshotReadResult snapshot = await _snapshotReader.ReadAsync(
            new ReplicaSnapshotReadRequest(snapshotPath, password),
            null,
            cancellationToken).ConfigureAwait(false);
        if (snapshot.Manifest.SourcePlatform != ReplicaPlatformFamily.Windows)
        {
            throw new ReplicaSnapshotException(
                "This snapshot was created on another platform and cannot be used for a Windows restore plan.");
        }

        EnvironmentScanResult current = await _environmentScanner.ScanAsync(
            null,
            cancellationToken).ConfigureAwait(false);
        if (!current.IsRestorePlanningComplete)
        {
            throw new ReplicaSnapshotException(
                "The current computer scan is incomplete, so a restore plan cannot be created safely.");
        }

        DiffEnvironmentState source = BuildSnapshotState(snapshot);
        DiffEnvironmentState target = BuildCurrentState(current);
        EnvironmentDiffResult diff = _diffEngine.Compare(source, target, mode, cancellationToken);
        return new SnapshotEnvironmentComparisonResult(snapshot.Manifest, current, source, target, diff);
    }

    private static DiffEnvironmentState BuildSnapshotState(ReplicaSnapshotReadResult snapshot)
    {
        (DiffApplicationEntry[] applications, DiffApplicationEntry[] storeApplications) =
            MapApplications(snapshot.Inventory.Applications, fromSnapshot: true);
        List<DiffValueEntry> values = snapshot.Inventory.Environment.Variables
            .Select(variable => EnvironmentValue(variable.Name, variable.Value, variable.Scope))
            .ToList();
        AddWindowsValues(values, snapshot.Inventory.Windows);
        values.AddRange(snapshot.Inventory.Fonts.Select(font => new DiffValueEntry(
            DiffArea.Fonts,
            $"{font.FamilyName}:{font.FaceName}",
            font.FamilyName,
            font.Version,
            CanRestoreAutomatically: false)));
        AddPluginValues(values, snapshot.Inventory.Plugins.Select(plugin => (
            plugin.PluginId,
            plugin.Values ?? new Dictionary<string, string>(StringComparer.Ordinal),
            plugin.Files ?? [])));
        values.AddRange(snapshot.Manifest.Exclusions
            .Where(exclusion => exclusion.ReasonCode.Contains("Sensitive", StringComparison.OrdinalIgnoreCase))
            .Select(exclusion => new DiffValueEntry(
                DiffArea.EnvironmentVariables,
                $"excluded:{exclusion.Path}",
                exclusion.Path,
                IsSensitiveExcluded: true)));

        return new DiffEnvironmentState(
            applications,
            storeApplications,
            values,
            snapshot.Inventory.Environment.PathEntries.Select(path => new DiffPathEntry(
                path.Value,
                path.Scope,
                path.Order,
                RequiresAdministrator: path.Scope.Equals("Machine", StringComparison.OrdinalIgnoreCase)))
                .ToArray());
    }

    private static DiffEnvironmentState BuildCurrentState(EnvironmentScanResult scan)
    {
        (DiffApplicationEntry[] applications, DiffApplicationEntry[] storeApplications) =
            MapApplications(scan.Applications, fromSnapshot: false);
        List<DiffValueEntry> values = scan.EnvironmentVariables.Select(variable => new DiffValueEntry(
            DiffArea.EnvironmentVariables,
            $"{variable.Scope}:{variable.Name}",
            variable.Name,
            variable.Value,
            IsSensitiveExcluded: variable.IsSensitive,
            CanRestoreAutomatically: !variable.IsSensitive,
            RequiresAdministrator: variable.Scope.Equals("Machine", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (scan.Windows is not null)
        {
            AddWindowsValues(values, scan.Windows);
        }

        values.AddRange(scan.Fonts.Select(font => new DiffValueEntry(
            DiffArea.Fonts,
            $"{font.FamilyName}:{font.Style}",
            font.FamilyName,
            font.Style,
            CanRestoreAutomatically: font.IsRestorable)));
        AddPluginValues(values, (scan.BuiltInPluginSnapshots ?? []).Select(snapshot => (
            snapshot.PluginId,
            (IReadOnlyDictionary<string, string>)snapshot.Values,
            (IReadOnlyList<ReplicaPluginFile>)snapshot.Files.Select(file => new ReplicaPluginFile(
                file.LogicalPath,
                file.Content,
                file.ContentType)).ToArray())));

        return new DiffEnvironmentState(
            applications,
            storeApplications,
            values,
            scan.PathEntries.Select(path => new DiffPathEntry(
                path.Value,
                path.Scope,
                path.Order,
                CanRestoreAutomatically: !path.IsDuplicate,
                RequiresAdministrator: path.Scope.Equals("Machine", StringComparison.OrdinalIgnoreCase)))
                .ToArray());
    }

    private static (DiffApplicationEntry[] Applications, DiffApplicationEntry[] StoreApplications)
        MapApplications<T>(IEnumerable<T> inventory, bool fromSnapshot)
    {
        List<DiffApplicationEntry> applications = [];
        List<DiffApplicationEntry> storeApplications = [];
        foreach (T item in inventory)
        {
            ApplicationDescriptor descriptor;
            bool isStore;
            bool canRestore;
            if (fromSnapshot && item is ReplicaApplication snapshot)
            {
                descriptor = ApplicationDescriptor.FromSnapshotApplication(snapshot);
                isStore = snapshot.PackageIdentity.MsixPackageFamilyName is not null;
                canRestore = snapshot.PackageIdentity.WingetPackageId is not null;
            }
            else if (!fromSnapshot && item is ScannedApplication scanned)
            {
                descriptor = ApplicationDescriptor.FromScannedApplication(scanned);
                isStore = scanned.PackageFamilyName is not null;
                canRestore = scanned.IsRestorable;
            }
            else
            {
                throw new InvalidDataException("Unsupported application inventory type.");
            }

            DiffApplicationEntry entry = new(descriptor, CanRestoreAutomatically: canRestore);
            (isStore ? storeApplications : applications).Add(entry);
        }

        return (applications.ToArray(), storeApplications.ToArray());
    }

    private static DiffValueEntry EnvironmentValue(string name, string value, string scope)
    {
        bool isSensitive = SensitiveEnvironmentPolicy.IsSensitiveName(name);
        return new DiffValueEntry(
            DiffArea.EnvironmentVariables,
            $"{scope}:{name}",
            name,
            isSensitive ? null : value,
            IsSensitiveExcluded: isSensitive,
            CanRestoreAutomatically: !isSensitive,
            RequiresAdministrator: scope.Equals("Machine", StringComparison.OrdinalIgnoreCase));
    }

    private static void AddWindowsValues(
        ICollection<DiffValueEntry> values,
        ReplicaWindowsInfo windows)
    {
        AddWindowsValues(
            values,
            windows.Edition,
            windows.Version,
            windows.Build,
            windows.Architecture,
            windows.Locale,
            windows.TimeZone);
    }

    private static void AddWindowsValues(
        ICollection<DiffValueEntry> values,
        WindowsEnvironmentInfo windows)
    {
        AddWindowsValues(
            values,
            windows.Edition,
            windows.Version,
            windows.Build,
            windows.Architecture,
            windows.Locale,
            windows.TimeZone);
    }

    private static void AddWindowsValues(
        ICollection<DiffValueEntry> values,
        string edition,
        string version,
        string build,
        string architecture,
        string locale,
        string timeZone)
    {
        values.Add(Value("Edition", edition));
        values.Add(Value("Version", version));
        values.Add(Value("Build", build));
        values.Add(Value("Architecture", architecture));
        values.Add(Value("Locale", locale));
        values.Add(Value("TimeZone", timeZone));

        static DiffValueEntry Value(string key, string value) => new(
            DiffArea.WindowsInformation,
            key,
            key,
            value,
            CanRestoreAutomatically: false);
    }

    private static void AddPluginValues(
        ICollection<DiffValueEntry> values,
        IEnumerable<(string PluginId, IReadOnlyDictionary<string, string> Values,
            IReadOnlyList<ReplicaPluginFile> Files)> plugins)
    {
        foreach ((string pluginId, IReadOnlyDictionary<string, string> pluginValues,
                     IReadOnlyList<ReplicaPluginFile> files) in plugins)
        {
            foreach ((string key, string value) in pluginValues)
            {
                values.Add(new DiffValueEntry(
                    DiffArea.PluginSettings,
                    $"{pluginId}:{key}",
                    key,
                    value,
                    CanRestoreAutomatically: false));
            }

            foreach (ReplicaPluginFile file in files)
            {
                values.Add(new DiffValueEntry(
                    DiffArea.ConfigurationFiles,
                    $"{pluginId}:{file.LogicalPath}",
                    file.LogicalPath,
                    Hash: Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file.Content))),
                    CanRestoreAutomatically: false));
            }
        }
    }
}
