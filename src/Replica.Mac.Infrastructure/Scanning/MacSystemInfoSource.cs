using System.Globalization;
using System.Runtime.InteropServices;
using System.Xml;
using Replica.Core.Snapshots;

namespace Replica.Mac.Infrastructure.Scanning;

public sealed class MacSystemInfoSource : IMacSystemInfoSource
{
    private const string SystemVersionPath = "/System/Library/CoreServices/SystemVersion.plist";

    public Task<(ReplicaPlatformInfo Platform, string MachineName)> ReadAsync(
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("The macOS scanner can run only on macOS.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyDictionary<string, string> values;
        try
        {
            values = File.Exists(SystemVersionPath)
                ? MacPropertyListReader.ReadStringDictionary(SystemVersionPath)
                : new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or XmlException)
        {
            values = new Dictionary<string, string>(StringComparer.Ordinal);
        }
        string version = values.GetValueOrDefault("ProductVersion") ?? Environment.OSVersion.Version.ToString();
        string build = values.GetValueOrDefault("ProductBuildVersion") ?? version;
        string productName = values.GetValueOrDefault("ProductName") ?? "macOS";
        string architecture = RuntimeInformation.OSArchitecture.ToString();
        string locale = CultureInfo.CurrentUICulture.Name;
        string timeZone = TimeZoneInfo.Local.Id;
        ReplicaPlatformInfo platform = new(
            ReplicaPlatformFamily.MacOS,
            productName,
            version,
            build,
            architecture,
            locale,
            timeZone,
            ["MacOS", "ApplicationBundles", "HomebrewInventory", "SnapshotV1"]);
        return Task.FromResult((platform, Environment.MachineName));
    }
}
