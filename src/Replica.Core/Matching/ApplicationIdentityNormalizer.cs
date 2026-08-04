using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Replica.Core.Services;

namespace Replica.Core.Matching;

public sealed partial class ApplicationIdentityNormalizer : IApplicationIdentityNormalizer
{
    private static readonly HashSet<string> PublisherSuffixes = new(StringComparer.Ordinal)
    {
        "ag",
        "co",
        "company",
        "corp",
        "corporation",
        "gmbh",
        "inc",
        "incorporated",
        "limited",
        "llc",
        "ltd",
        "plc",
        "sa",
    };

    private static readonly HashSet<string> EditionNames = new(StringComparer.Ordinal)
    {
        "community",
        "education",
        "enterprise",
        "home",
        "professional",
        "pro",
        "server",
        "standard",
        "ultimate",
    };

    public NormalizedApplicationIdentity Normalize(ApplicationDescriptor application)
    {
        ArgumentNullException.ThrowIfNull(application);
        string displayName = application.DisplayName ?? string.Empty;
        string architecture = NormalizeArchitecture(application.Architecture);
        if (architecture.Length == 0)
        {
            architecture = DetectArchitecture(displayName);
        }

        string name = NormalizeName(displayName);
        string[] tokens = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        ApplicationReleaseChannel channel = DetectReleaseChannel(tokens);
        ApplicationComponentKind component = DetectComponent(tokens);
        string[] editions = tokens
            .Where(EditionNames.Contains)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        return new NormalizedApplicationIdentity(
            name,
            NormalizePublisher(application.Publisher),
            NormalizeInstallLocation(application.InstallLocation),
            architecture,
            channel,
            component,
            editions);
    }

    public string NormalizeName(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string normalized = TrademarkCharacters().Replace(value, string.Empty)
            .Normalize(NormalizationForm.FormKC);
        normalized = ParenthesizedInstallQualifier().Replace(normalized, " ");
        normalized = ArchitectureToken().Replace(normalized, " ");
        normalized = DottedOrPrefixedVersion().Replace(normalized, " ");
        normalized = YearVersion().Replace(normalized, " ");
        normalized = GenericEditionWord().Replace(normalized, " ");
        return NormalizeCharacters(normalized);
    }

    public string NormalizePublisher(string? value)
    {
        string normalized = NormalizeCharacters(value ?? string.Empty);
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        List<string> tokens = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        while (tokens.Count > 0 && PublisherSuffixes.Contains(tokens[^1]))
        {
            tokens.RemoveAt(tokens.Count - 1);
        }

        return string.Join(' ', tokens);
    }

    public string NormalizeInstallLocation(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value
            .Trim()
            .Trim('"')
            .Replace('/', '\\')
            .TrimEnd('\\')
            .ToUpperInvariant();
    }

    private static string NormalizeCharacters(string value)
    {
        string decomposed = value.Normalize(NormalizationForm.FormD);
        StringBuilder result = new(decomposed.Length);
        bool pendingSpace = false;
        foreach (char character in decomposed)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                if (pendingSpace && result.Length > 0)
                {
                    result.Append(' ');
                }

                result.Append(char.ToLowerInvariant(character));
                pendingSpace = false;
            }
            else
            {
                pendingSpace = true;
            }
        }

        return result.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string NormalizeArchitecture(string? architecture)
    {
        if (string.IsNullOrWhiteSpace(architecture))
        {
            return string.Empty;
        }

        string normalized = architecture.Trim().ToUpperInvariant();
        return normalized switch
        {
            "AMD64" or "X64" or "64-BIT" or "64BIT" => "X64",
            "I386" or "I686" or "X86" or "32-BIT" or "32BIT" => "X86",
            "AARCH64" or "ARM64" => "ARM64",
            "ARM" => "ARM",
            _ => normalized,
        };
    }

    private static string DetectArchitecture(string value)
    {
        if (X64Token().IsMatch(value))
        {
            return "X64";
        }

        if (X86Token().IsMatch(value))
        {
            return "X86";
        }

        if (Arm64Token().IsMatch(value))
        {
            return "ARM64";
        }

        return string.Empty;
    }

    private static ApplicationReleaseChannel DetectReleaseChannel(
        IReadOnlyCollection<string> tokens)
    {
        if (tokens.Contains("preview") ||
            tokens.Contains("insider") ||
            tokens.Contains("canary") ||
            tokens.Contains("nightly"))
        {
            return ApplicationReleaseChannel.Preview;
        }

        if (tokens.Contains("beta"))
        {
            return ApplicationReleaseChannel.Beta;
        }

        return tokens.Contains("rc")
            ? ApplicationReleaseChannel.ReleaseCandidate
            : ApplicationReleaseChannel.Stable;
    }

    private static ApplicationComponentKind DetectComponent(IReadOnlyCollection<string> tokens)
    {
        if (tokens.Contains("sdk"))
        {
            return ApplicationComponentKind.Sdk;
        }

        return tokens.Contains("runtime") || tokens.Contains("redistributable")
            ? ApplicationComponentKind.Runtime
            : ApplicationComponentKind.Application;
    }

    [GeneratedRegex("[™®©℠]", RegexOptions.CultureInvariant)]
    private static partial Regex TrademarkCharacters();

    [GeneratedRegex(
        @"\((?:per[\s-]?user|user|user install|machine[\s-]?wide|system|사용자|사용자용|시스템)\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ParenthesizedInstallQualifier();

    [GeneratedRegex(
        @"\b(?:x64|x86|amd64|i386|i686|aarch64|arm64|64[\s-]?bit|32[\s-]?bit)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArchitectureToken();

    [GeneratedRegex(
        @"(?<![\p{L}\p{N}])(?:(?:v\s*)\d{1,4}(?:[._-]\d+){0,3}|\d{1,4}(?:[._-]\d+){1,3})(?![\p{L}\p{N}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DottedOrPrefixedVersion();

    [GeneratedRegex(
        @"(?<![\p{L}\p{N}])(?:19|20)\d{2}(?![\p{L}\p{N}])",
        RegexOptions.CultureInvariant)]
    private static partial Regex YearVersion();

    [GeneratedRegex(@"\bedition\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GenericEditionWord();

    [GeneratedRegex(
        @"\b(?:x64|amd64|64[\s-]?bit)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex X64Token();

    [GeneratedRegex(
        @"\b(?:x86|i386|i686|32[\s-]?bit)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex X86Token();

    [GeneratedRegex(
        @"\b(?:arm64|aarch64)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Arm64Token();
}
