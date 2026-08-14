using System.Buffers.Binary;
using System.Xml;
using Replica.Core.Platforms;

namespace Replica.Mac.Infrastructure.Scanning;

public sealed class MacApplicationSource : IMacApplicationSource
{
    private const int MaximumApplications = 5_000;
    private const int MaximumDirectories = 50_000;
    private const int MaximumDepth = 4;
    private readonly IReadOnlyList<string> roots;
    private readonly IMacPropertyListReader propertyListReader;

    public MacApplicationSource()
        : this(CreateDefaultRoots(), new MacPropertyListReader())
    {
    }

    public MacApplicationSource(IReadOnlyList<string> roots)
        : this(roots, new MacPropertyListReader())
    {
    }

    public MacApplicationSource(IReadOnlyList<string> roots, IMacPropertyListReader propertyListReader)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(propertyListReader);
        this.roots = roots.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).ToArray();
        this.propertyListReader = propertyListReader;
    }

    public async Task<IReadOnlyList<PlatformApplication>> ReadAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> bundles = await Task.Run(
            () => EnumerateBundles(cancellationToken),
            cancellationToken).ConfigureAwait(false);
        Dictionary<string, PlatformApplication> applications = new(StringComparer.Ordinal);
        foreach (string bundle in bundles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PlatformApplication application = await ReadBundleAsync(bundle, cancellationToken).ConfigureAwait(false);
            string identity = application.BundleIdentifier ?? bundle;
            applications.TryAdd(identity, application);
        }

        return applications.Values.OrderBy(app => app.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private IReadOnlyList<string> EnumerateBundles(CancellationToken cancellationToken)
    {
        List<string> bundles = [];
        int visitedDirectories = 0;
        foreach (string root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(root) || IsReparsePoint(root))
            {
                continue;
            }

            Queue<(string Path, int Depth)> pending = new();
            pending.Enqueue((root, 0));
            while (pending.Count > 0 &&
                   bundles.Count < MaximumApplications &&
                   visitedDirectories < MaximumDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                (string current, int depth) = pending.Dequeue();
                IEnumerable<string> directories;
                try
                {
                    directories = Directory.EnumerateDirectories(current);
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
                {
                    continue;
                }

                foreach (string directory in directories)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    visitedDirectories++;
                    if (visitedDirectories > MaximumDirectories)
                    {
                        break;
                    }

                    if (IsReparsePoint(directory))
                    {
                        continue;
                    }

                    if (Path.GetExtension(directory).Equals(".app", StringComparison.OrdinalIgnoreCase))
                    {
                        bundles.Add(Path.GetFullPath(directory));
                        if (bundles.Count >= MaximumApplications)
                        {
                            break;
                        }

                        continue;
                    }

                    if (depth < MaximumDepth)
                    {
                        pending.Enqueue((directory, depth + 1));
                    }
                }
            }
        }

        return bundles;
    }

    private async Task<PlatformApplication> ReadBundleAsync(
        string bundlePath,
        CancellationToken cancellationToken)
    {
        string fallbackName = Path.GetFileNameWithoutExtension(bundlePath);
        string? version = null;
        string? bundleIdentifier = null;
        string? executableName = null;
        string name = fallbackName;
        string plistPath = Path.Combine(bundlePath, "Contents", "Info.plist");
        try
        {
            if (File.Exists(plistPath))
            {
                IReadOnlyDictionary<string, string> values = await propertyListReader
                    .ReadStringDictionaryAsync(plistPath, cancellationToken)
                    .ConfigureAwait(false);
                name = values.GetValueOrDefault("CFBundleDisplayName")
                    ?? values.GetValueOrDefault("CFBundleName")
                    ?? fallbackName;
                version = values.GetValueOrDefault("CFBundleShortVersionString")
                    ?? values.GetValueOrDefault("CFBundleVersion");
                bundleIdentifier = values.GetValueOrDefault("CFBundleIdentifier");
                executableName = values.GetValueOrDefault("CFBundleExecutable");
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or XmlException or TimeoutException)
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
            DetectArchitecture(bundlePath, executableName),
            IsRestorable: false);
    }

    private static string DetectArchitecture(string bundlePath, string? executableName)
    {
        if (string.IsNullOrWhiteSpace(executableName) ||
            executableName.Length > 255 ||
            executableName.Any(character => character is '/' or '\\' || char.IsControl(character)))
        {
            return "Unknown";
        }

        string executablePath = Path.Combine(bundlePath, "Contents", "MacOS", executableName);
        try
        {
            FileInfo executable = new(executablePath);
            if (!executable.Exists || (executable.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return "Unknown";
            }

            Span<byte> header = stackalloc byte[8];
            using FileStream stream = new(executablePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            stream.ReadExactly(header);

            uint magicBigEndian = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (magicBigEndian is 0xCAFEBABE or 0xCAFEBABF)
            {
                return "Universal";
            }

            bool littleEndian = magicBigEndian is 0xCEFAEDFE or 0xCFFAEDFE;
            uint cpuType = littleEndian
                ? BinaryPrimitives.ReadUInt32LittleEndian(header[4..])
                : BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
            return cpuType switch
            {
                0x0100000C => "Arm64",
                0x01000007 => "x64",
                0x00000007 => "x86",
                _ => "Unknown",
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "Unknown";
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
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
        return ["/Applications", Path.Combine(profile, "Applications"), "/System/Applications"];
    }
}
