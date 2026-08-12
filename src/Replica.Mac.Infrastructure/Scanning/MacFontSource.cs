using Replica.Core.Platforms;

namespace Replica.Mac.Infrastructure.Scanning;

public sealed class MacFontSource : IMacFontSource
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ttf", ".ttc", ".otf", ".dfont",
    };

    private const int MaximumFonts = 20_000;
    private readonly IReadOnlyList<(string Path, string Scope)> roots;

    public MacFontSource()
        : this(CreateDefaultRoots())
    {
    }

    public MacFontSource(IReadOnlyList<(string Path, string Scope)> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        this.roots = roots;
    }

    public Task<IReadOnlyList<PlatformFont>> ReadAsync(CancellationToken cancellationToken)
    {
        return Task.Run<IReadOnlyList<PlatformFont>>(() => Read(cancellationToken), cancellationToken);
    }

    private IReadOnlyList<PlatformFont> Read(CancellationToken cancellationToken)
    {
        List<PlatformFont> fonts = [];
        foreach ((string path, string scope) in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(path))
            {
                continue;
            }

            try
            {
                EnumerationOptions options = new()
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                    MaxRecursionDepth = 8,
                };
                foreach (string file in Directory.EnumerateFiles(path, "*", options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!Extensions.Contains(Path.GetExtension(file)))
                    {
                        continue;
                    }

                    string family = Path.GetFileNameWithoutExtension(file);
                    fonts.Add(new PlatformFont(family, "Unknown", scope, Path.GetFullPath(file)));
                    if (fonts.Count >= MaximumFonts)
                    {
                        return fonts;
                    }
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                continue;
            }
        }

        return fonts.OrderBy(font => font.FamilyName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<(string Path, string Scope)> CreateDefaultRoots()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return
        [
            (Path.Combine(profile, "Library", "Fonts"), "User"),
            ("/Library/Fonts", "System"),
            ("/System/Library/Fonts", "System"),
        ];
    }
}
