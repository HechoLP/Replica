using System.Xml;
using Replica.Core.Platforms;

namespace Replica.Mac.Infrastructure.Scanning;

public sealed class MacApplicationSource : IMacApplicationSource
{
    private const int MaximumApplications = 5_000;
    private readonly IReadOnlyList<string> roots;

    public MacApplicationSource()
        : this(CreateDefaultRoots())
    {
    }

    public MacApplicationSource(IReadOnlyList<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        this.roots = roots.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).ToArray();
    }

    public Task<IReadOnlyList<PlatformApplication>> ReadAsync(CancellationToken cancellationToken)
    {
        return Task.Run<IReadOnlyList<PlatformApplication>>(
            () => Read(cancellationToken),
            cancellationToken);
    }

    private IReadOnlyList<PlatformApplication> Read(CancellationToken cancellationToken)
    {
        Dictionary<string, PlatformApplication> applications = new(StringComparer.Ordinal);
        foreach (string root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(root))
            {
                continue;
            }

            IEnumerable<string> bundles;
            try
            {
                bundles = Directory.EnumerateDirectories(root, "*.app", SearchOption.TopDirectoryOnly);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string bundle in bundles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (applications.Count >= MaximumApplications)
                {
                    return applications.Values.OrderBy(app => app.Name, StringComparer.OrdinalIgnoreCase).ToArray();
                }

                PlatformApplication application = ReadBundle(bundle);
                string identity = application.BundleIdentifier ?? bundle;
                applications.TryAdd(identity, application);
            }
        }

        return applications.Values.OrderBy(app => app.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static PlatformApplication ReadBundle(string bundlePath)
    {
        string fallbackName = Path.GetFileNameWithoutExtension(bundlePath);
        string? version = null;
        string? bundleIdentifier = null;
        string name = fallbackName;
        string plistPath = Path.Combine(bundlePath, "Contents", "Info.plist");
        try
        {
            if (File.Exists(plistPath))
            {
                IReadOnlyDictionary<string, string> values = MacPropertyListReader.ReadStringDictionary(plistPath);
                name = values.GetValueOrDefault("CFBundleDisplayName")
                    ?? values.GetValueOrDefault("CFBundleName")
                    ?? fallbackName;
                version = values.GetValueOrDefault("CFBundleShortVersionString")
                    ?? values.GetValueOrDefault("CFBundleVersion");
                bundleIdentifier = values.GetValueOrDefault("CFBundleIdentifier");
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or XmlException)
        {
            // The bundle remains useful inventory even when its metadata is unreadable.
        }

        return new PlatformApplication(
            name,
            version,
            PublisherFromIdentifier(bundleIdentifier),
            Path.GetFullPath(bundlePath),
            bundleIdentifier,
            null,
            "ApplicationBundle",
            "Unknown",
            IsRestorable: false);
    }

    private static string? PublisherFromIdentifier(string? bundleIdentifier)
    {
        if (string.IsNullOrWhiteSpace(bundleIdentifier))
        {
            return null;
        }

        string[] parts = bundleIdentifier.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[1] : null;
    }

    private static IReadOnlyList<string> CreateDefaultRoots()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return ["/Applications", Path.Combine(profile, "Applications")];
    }
}
