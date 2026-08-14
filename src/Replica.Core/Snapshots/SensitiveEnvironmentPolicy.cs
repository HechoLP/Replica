namespace Replica.Core.Snapshots;

public static class SensitiveEnvironmentPolicy
{
    private static readonly string[] SensitiveMarkers =
    [
        "CONNECTION_STRING",
        "CREDENTIAL",
        "PASSWORD",
        "PASSWD",
        "SECRET",
        "TOKEN",
        "COOKIE",
        "AUTH",
        "JWT",
        "KEY",
    ];

    public static bool IsSensitiveName(string name)
    {
        return FindSensitiveMarker(name) is not null;
    }

    public static string? FindSensitiveMarker(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string normalized = name.Replace('-', '_').ToUpperInvariant();
        return SensitiveMarkers.FirstOrDefault(marker =>
            normalized.Contains(marker, StringComparison.Ordinal));
    }
}
