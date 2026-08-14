namespace Replica.Core.Snapshots;

public static class SnapshotOpenArgumentsParser
{
    public const string OpenSwitch = "--open-snapshot";

    public static bool TryParse(IReadOnlyList<string> arguments, out string? snapshotPath)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        snapshotPath = null;

        if (arguments.Count != 2 ||
            !arguments[0].Equals(OpenSwitch, StringComparison.Ordinal) ||
            !TryNormalizePath(arguments[1], out snapshotPath))
        {
            snapshotPath = null;
            return false;
        }

        return true;
    }

    public static bool TryParseAssociatedFile(
        IReadOnlyList<string> arguments,
        out string? snapshotPath)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        snapshotPath = null;
        return arguments.Count == 1 && TryNormalizePath(arguments[0], out snapshotPath);
    }

    private static bool TryNormalizePath(string path, out string? snapshotPath)
    {
        snapshotPath = null;
        if (string.IsNullOrWhiteSpace(path) ||
            path.Length > 32_767 ||
            path.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(path) ||
            !Path.GetExtension(path).Equals(".replica", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            snapshotPath = Path.GetFullPath(path);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
