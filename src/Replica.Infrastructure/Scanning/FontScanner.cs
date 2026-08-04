using Microsoft.Win32;
using Replica.Core.Scanning;
using Replica.Core.Services;

namespace Replica.Infrastructure.Scanning;

public interface IFontInventorySource
{
    Task<IReadOnlyList<FontRegistryRecord>> ReadAsync(CancellationToken cancellationToken);
}

public sealed record FontRegistryRecord(
    string DisplayName,
    string FilePath,
    string Scope);

public sealed class WindowsFontInventorySource : IFontInventorySource
{
    private const string FontsRegistryPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts";

    public Task<IReadOnlyList<FontRegistryRecord>> ReadAsync(
        CancellationToken cancellationToken)
    {
        return Task.Run<IReadOnlyList<FontRegistryRecord>>(
            () =>
            {
                List<FontRegistryRecord> fonts = [];
                ReadHive(RegistryHive.LocalMachine, "System", fonts, cancellationToken);
                ReadHive(RegistryHive.CurrentUser, "User", fonts, cancellationToken);
                return fonts;
            },
            cancellationToken);
    }

    private static void ReadHive(
        RegistryHive hive,
        string scope,
        ICollection<FontRegistryRecord> fonts,
        CancellationToken cancellationToken)
    {
        using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
        using RegistryKey? key = baseKey.OpenSubKey(FontsRegistryPath, writable: false);
        if (key is null)
        {
            return;
        }

        foreach (string valueName in key.GetValueNames())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (key.GetValue(valueName) is not string path || string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            fonts.Add(new FontRegistryRecord(valueName, ResolveFontPath(path, scope), scope));
        }
    }

    private static string ResolveFontPath(string path, string scope)
    {
        string expanded = System.Environment.ExpandEnvironmentVariables(path);
        if (Path.IsPathFullyQualified(expanded))
        {
            return expanded;
        }

        string root = scope.Equals("User", StringComparison.Ordinal)
            ? Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "Microsoft",
                "Windows",
                "Fonts")
            : System.Environment.GetFolderPath(System.Environment.SpecialFolder.Fonts);
        return Path.Combine(root, expanded);
    }
}

public sealed class FontScanner : IFontScanner
{
    private static readonly string[] Styles =
    [
        "Bold Italic",
        "SemiBold Italic",
        "Light Italic",
        "ExtraBold",
        "SemiBold",
        "Regular",
        "Italic",
        "Bold",
        "Light",
        "Medium",
    ];

    private readonly IFontInventorySource _source;

    public FontScanner(IFontInventorySource source)
    {
        _source = source;
    }

    public async Task<FontScanResult> ScanAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<FontRegistryRecord> records = await _source
            .ReadAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<string, ScannedFont> unique = new(StringComparer.OrdinalIgnoreCase);
        foreach (FontRegistryRecord record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (string family, string style) = ParseDisplayName(record.DisplayName);
            string identity = $"{record.Scope}|{record.FilePath}";
            unique.TryAdd(
                identity,
                new ScannedFont(
                    family,
                    style,
                    record.Scope,
                    record.FilePath,
                    IsRestorable: false));
        }

        return new FontScanResult(
            unique.Values
                .OrderBy(font => font.FamilyName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(font => font.Style, StringComparer.CurrentCultureIgnoreCase)
                .ToArray(),
            []);
    }

    private static (string Family, string Style) ParseDisplayName(string displayName)
    {
        string cleaned = displayName;
        int typeSuffix = cleaned.LastIndexOf('(');
        if (typeSuffix > 0)
        {
            cleaned = cleaned[..typeSuffix].Trim();
        }

        foreach (string style in Styles)
        {
            if (cleaned.EndsWith($" {style}", StringComparison.OrdinalIgnoreCase))
            {
                return (cleaned[..^(style.Length + 1)].Trim(), style);
            }
        }

        return (cleaned, "Regular");
    }
}
