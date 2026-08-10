using System.Globalization;

namespace Replica.Core.Updates;

public enum UpdateChannel
{
    Stable,
    Beta,
    Alpha,
}

public enum SemanticPrereleaseKind
{
    Alpha = 0,
    Beta = 1,
    ReleaseCandidate = 2,
    Stable = 3,
}

public sealed record SemanticVersion(
    int Major,
    int Minor,
    int Patch,
    SemanticPrereleaseKind PrereleaseKind = SemanticPrereleaseKind.Stable,
    int PrereleaseNumber = 0) : IComparable<SemanticVersion>
{
    public bool IsPrerelease => PrereleaseKind != SemanticPrereleaseKind.Stable;

    public UpdateChannel Channel => PrereleaseKind switch
    {
        SemanticPrereleaseKind.Alpha => UpdateChannel.Alpha,
        SemanticPrereleaseKind.Beta or SemanticPrereleaseKind.ReleaseCandidate => UpdateChannel.Beta,
        _ => UpdateChannel.Stable,
    };

    public static SemanticVersion FromVersion(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return new SemanticVersion(
            Math.Max(version.Major, 0),
            Math.Max(version.Minor, 0),
            Math.Max(version.Build, 0));
    }

    public static bool TryParse(string? value, out SemanticVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string normalized = value.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
        {
            normalized = normalized[1..];
        }

        string[] mainAndPrerelease = normalized.Split('-', 2, StringSplitOptions.None);
        string[] numbers = mainAndPrerelease[0].Split('.', StringSplitOptions.None);
        if (numbers.Length != 3 ||
            !TryParseNumber(numbers[0], out int major) ||
            !TryParseNumber(numbers[1], out int minor) ||
            !TryParseNumber(numbers[2], out int patch))
        {
            return false;
        }

        if (mainAndPrerelease.Length == 1)
        {
            version = new SemanticVersion(major, minor, patch);
            return true;
        }

        string[] prerelease = mainAndPrerelease[1].Split('.', StringSplitOptions.None);
        if (prerelease.Length != 2 ||
            !TryParseNumber(prerelease[1], out int prereleaseNumber))
        {
            return false;
        }

        SemanticPrereleaseKind kind = prerelease[0].ToLowerInvariant() switch
        {
            "alpha" => SemanticPrereleaseKind.Alpha,
            "beta" => SemanticPrereleaseKind.Beta,
            "rc" => SemanticPrereleaseKind.ReleaseCandidate,
            _ => (SemanticPrereleaseKind)(-1),
        };
        if (!Enum.IsDefined(kind))
        {
            return false;
        }

        version = new SemanticVersion(major, minor, patch, kind, prereleaseNumber);
        return true;
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        int comparison = Major.CompareTo(other.Major);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = Minor.CompareTo(other.Minor);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = Patch.CompareTo(other.Patch);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = PrereleaseKind.CompareTo(other.PrereleaseKind);
        return comparison != 0 ? comparison : PrereleaseNumber.CompareTo(other.PrereleaseNumber);
    }

    public override string ToString()
    {
        string core = string.Create(
            CultureInfo.InvariantCulture,
            $"{Major}.{Minor}.{Patch}");
        return PrereleaseKind switch
        {
            SemanticPrereleaseKind.Alpha => $"{core}-alpha.{PrereleaseNumber}",
            SemanticPrereleaseKind.Beta => $"{core}-beta.{PrereleaseNumber}",
            SemanticPrereleaseKind.ReleaseCandidate => $"{core}-rc.{PrereleaseNumber}",
            _ => core,
        };
    }

    private static bool TryParseNumber(string value, out int number)
    {
        number = 0;
        if (value.Length == 0 ||
            (value.Length > 1 && value[0] == '0') ||
            !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number))
        {
            return false;
        }

        return number >= 0;
    }
}
