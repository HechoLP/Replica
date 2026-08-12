namespace Replica.Mac.Infrastructure.Scanning;

public static class SensitiveEnvironmentPolicy
{
    private static readonly string[] SensitiveFragments =
    [
        "TOKEN",
        "SECRET",
        "PASSWORD",
        "PASSWD",
        "API_KEY",
        "PRIVATE_KEY",
        "RECOVERY_KEY",
        "CREDENTIAL",
        "CONNECTION_STRING",
    ];

    public static bool IsSensitive(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string normalized = name.Replace('-', '_').ToUpperInvariant();
        return SensitiveFragments.Any(fragment => normalized.Contains(fragment, StringComparison.Ordinal));
    }
}
