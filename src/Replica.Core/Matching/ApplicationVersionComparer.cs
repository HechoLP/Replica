using System.Globalization;
using System.Text.RegularExpressions;
using Replica.Core.Services;

namespace Replica.Core.Matching;

public sealed partial class ApplicationVersionComparer : IApplicationVersionComparer
{
    public ApplicationVersionComparisonResult Compare(
        string? sourceVersion,
        string? targetVersion)
    {
        string? sourceText = NormalizeInput(sourceVersion);
        string? targetText = NormalizeInput(targetVersion);
        if (sourceText is null || targetText is null)
        {
            return new ApplicationVersionComparisonResult(
                ApplicationVersionComparison.Unknown,
                ApplicationVersionKind.Unknown,
                ApplicationVersionKind.Unknown,
                sourceText,
                targetText,
                "MissingVersion");
        }

        ParsedVersion? source = TryParse(sourceText);
        ParsedVersion? target = TryParse(targetText);
        if (source is null || target is null)
        {
            bool equal = sourceText.Equals(targetText, StringComparison.OrdinalIgnoreCase);
            return new ApplicationVersionComparisonResult(
                equal
                    ? ApplicationVersionComparison.Equal
                    : ApplicationVersionComparison.Incomparable,
                source?.Kind ?? ApplicationVersionKind.Unknown,
                target?.Kind ?? ApplicationVersionKind.Unknown,
                source?.Normalized ?? sourceText,
                target?.Normalized ?? targetText,
                equal ? "OpaqueVersionEqual" : "UnparseableVersion");
        }

        if (!KindsAreComparable(source.Kind, target.Kind))
        {
            return CreateResult(
                ApplicationVersionComparison.Incomparable,
                source,
                target,
                "VersionKindsDiffer");
        }

        if (source.Channel != target.Channel)
        {
            return CreateResult(
                ApplicationVersionComparison.Incomparable,
                source,
                target,
                "ReleaseChannelsDiffer");
        }

        int numericComparison = CompareParts(source.Parts, target.Parts);
        if (numericComparison == 0 && source.Channel != ApplicationReleaseChannel.Stable)
        {
            numericComparison = source.PreReleaseNumber.CompareTo(target.PreReleaseNumber);
        }

        ApplicationVersionComparison comparison = numericComparison switch
        {
            > 0 => ApplicationVersionComparison.SourceNewer,
            < 0 => ApplicationVersionComparison.TargetNewer,
            _ => ApplicationVersionComparison.Equal,
        };
        return CreateResult(comparison, source, target, "ParsedVersionComparison");
    }

    private static ParsedVersion? TryParse(string input)
    {
        if (input.Length > 128)
        {
            return null;
        }

        string withoutMetadata = input.Split('+', 2)[0].Trim();
        ApplicationReleaseChannel channel = ApplicationReleaseChannel.Stable;
        int preReleaseNumber = 0;
        Match channelMatch = PreRelease().Match(withoutMetadata);
        if (channelMatch.Success)
        {
            channel = ParseChannel(channelMatch.Groups["channel"].Value);
            if (channelMatch.Groups["number"].Success &&
                !int.TryParse(
                    channelMatch.Groups["number"].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out preReleaseNumber))
            {
                return null;
            }

            withoutMetadata = withoutMetadata[..channelMatch.Index]
                .TrimEnd(' ', '-', '.', '_');
        }

        Match compactDate = CompactDate().Match(withoutMetadata);
        if (compactDate.Success)
        {
            if (!TryParseDate(compactDate, out int[]? compactParts))
            {
                return null;
            }

            return new ParsedVersion(
                ApplicationVersionKind.Date,
                compactParts,
                channel,
                preReleaseNumber,
                FormatDate(compactParts));
        }

        Match separatedDate = SeparatedDate().Match(withoutMetadata);
        if (separatedDate.Success)
        {
            if (!TryParseDate(separatedDate, out int[]? dateParts))
            {
                return null;
            }

            return new ParsedVersion(
                ApplicationVersionKind.Date,
                dateParts,
                channel,
                preReleaseNumber,
                FormatDate(dateParts));
        }

        if (DateShapedVersion().IsMatch(withoutMetadata))
        {
            return null;
        }

        if (!NumericVersion().IsMatch(withoutMetadata))
        {
            return null;
        }

        string[] segments = withoutMetadata.Split('.');
        int[] parts = new int[segments.Length];
        for (int index = 0; index < segments.Length; index++)
        {
            if (!int.TryParse(
                    segments[index],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out parts[index]))
            {
                return null;
            }
        }

        ApplicationVersionKind kind = segments.Length == 3 ||
            channel != ApplicationReleaseChannel.Stable
            ? ApplicationVersionKind.Semantic
            : ApplicationVersionKind.System;
        string normalized = string.Join('.', parts);
        if (channel != ApplicationReleaseChannel.Stable)
        {
            normalized = $"{normalized}-{FormatChannel(channel)}.{preReleaseNumber}";
        }

        return new ParsedVersion(kind, parts, channel, preReleaseNumber, normalized);
    }

    private static string? NormalizeInput(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return VersionPrefix().Replace(value.Trim(), string.Empty);
    }

    private static bool TryParseDate(Match match, out int[] parts)
    {
        parts = [];
        if (!int.TryParse(match.Groups["year"].Value, out int year) ||
            !int.TryParse(match.Groups["month"].Value, out int month) ||
            !int.TryParse(match.Groups["day"].Value, out int day) ||
            !DateOnly.TryParseExact(
                $"{year:D4}-{month:D2}-{day:D2}",
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _))
        {
            return false;
        }

        int revision = 0;
        if (match.Groups["revision"].Success &&
            !int.TryParse(match.Groups["revision"].Value, out revision))
        {
            return false;
        }

        parts = match.Groups["revision"].Success
            ? [year, month, day, revision]
            : [year, month, day];
        return true;
    }

    private static int CompareParts(IReadOnlyList<int> source, IReadOnlyList<int> target)
    {
        int length = Math.Max(source.Count, target.Count);
        for (int index = 0; index < length; index++)
        {
            int sourcePart = index < source.Count ? source[index] : 0;
            int targetPart = index < target.Count ? target[index] : 0;
            int comparison = sourcePart.CompareTo(targetPart);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    private static bool KindsAreComparable(
        ApplicationVersionKind source,
        ApplicationVersionKind target)
    {
        return source == target ||
            (source is ApplicationVersionKind.Semantic or ApplicationVersionKind.System &&
             target is ApplicationVersionKind.Semantic or ApplicationVersionKind.System);
    }

    private static ApplicationReleaseChannel ParseChannel(string value)
    {
        string normalized = value.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        return normalized switch
        {
            "beta" => ApplicationReleaseChannel.Beta,
            "rc" or "releasecandidate" =>
                ApplicationReleaseChannel.ReleaseCandidate,
            _ => ApplicationReleaseChannel.Preview,
        };
    }

    private static string FormatChannel(ApplicationReleaseChannel channel)
    {
        return channel switch
        {
            ApplicationReleaseChannel.Preview => "preview",
            ApplicationReleaseChannel.Beta => "beta",
            ApplicationReleaseChannel.ReleaseCandidate => "rc",
            _ => "stable",
        };
    }

    private static string FormatDate(IReadOnlyList<int> parts)
    {
        string value = $"{parts[0]:D4}-{parts[1]:D2}-{parts[2]:D2}";
        return parts.Count == 4 ? $"{value}.{parts[3]}" : value;
    }

    private static ApplicationVersionComparisonResult CreateResult(
        ApplicationVersionComparison comparison,
        ParsedVersion source,
        ParsedVersion target,
        string reasonCode)
    {
        return new ApplicationVersionComparisonResult(
            comparison,
            source.Kind,
            target.Kind,
            source.Normalized,
            target.Normalized,
            reasonCode);
    }

    [GeneratedRegex(@"^v\s*(?=\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionPrefix();

    [GeneratedRegex(
        @"(?:^|[-._\s])(?<channel>preview|pre|alpha|beta|rc|release[\s-]?candidate)(?:[-._\s]?(?<number>\d+))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PreRelease();

    [GeneratedRegex(
        @"^(?<year>(?:19|20)\d{2})(?<month>0[1-9]|1[0-2])(?<day>0[1-9]|[12]\d|3[01])$",
        RegexOptions.CultureInvariant)]
    private static partial Regex CompactDate();

    [GeneratedRegex(
        @"^(?<year>(?:19|20)\d{2})[-._](?<month>0?[1-9]|1[0-2])[-._](?<day>0?[1-9]|[12]\d|3[01])(?:[-._](?<revision>\d+))?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SeparatedDate();

    [GeneratedRegex(
        @"^(?:19|20)\d{2}(?:[-._]\d{1,2}[-._]\d{1,2}|\d{4})",
        RegexOptions.CultureInvariant)]
    private static partial Regex DateShapedVersion();

    [GeneratedRegex(@"^\d+(?:\.\d+){0,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex NumericVersion();

    private sealed record ParsedVersion(
        ApplicationVersionKind Kind,
        IReadOnlyList<int> Parts,
        ApplicationReleaseChannel Channel,
        int PreReleaseNumber,
        string Normalized);
}
