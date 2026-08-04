namespace Replica.Infrastructure.Snapshots;

internal static class SnapshotEntryNames
{
    public const string Manifest = "manifest.json";
    public const string Applications = "inventory/applications.json";
    public const string Environment = "inventory/environment.json";
    public const string Windows = "inventory/windows.json";
    public const string Fonts = "inventory/fonts.json";
    public const string Plugins = "plugins/index.json";
    public const string Checksums = "checksums.json";
    public const string Exclusions = "metadata/exclusions.json";
    public const string Recovery = "metadata/recovery.json";

    public static IReadOnlySet<string> Required { get; } = new HashSet<string>(
        [
            Manifest,
            Applications,
            Environment,
            Windows,
            Fonts,
            Plugins,
            Checksums,
            Exclusions,
            Recovery,
        ],
        StringComparer.OrdinalIgnoreCase);
}
