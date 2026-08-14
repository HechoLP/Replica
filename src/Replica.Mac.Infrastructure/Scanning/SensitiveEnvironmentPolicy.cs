using CoreSensitiveEnvironmentPolicy = Replica.Core.Snapshots.SensitiveEnvironmentPolicy;

namespace Replica.Mac.Infrastructure.Scanning;

public static class SensitiveEnvironmentPolicy
{
    private static readonly HashSet<string> SafeValueNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "COLORTERM",
        "EDITOR",
        "HOMEBREW_PREFIX",
        "LANG",
        "LC_ALL",
        "PAGER",
        "SHELL",
        "TERM",
        "TERM_PROGRAM",
        "VISUAL",
    };

    public static bool IsSensitive(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return CoreSensitiveEnvironmentPolicy.IsSensitiveName(name);
    }

    public static bool CanCaptureValue(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return !IsSensitive(name) &&
            (SafeValueNames.Contains(name) || name.StartsWith("LC_", StringComparison.OrdinalIgnoreCase));
    }
}
