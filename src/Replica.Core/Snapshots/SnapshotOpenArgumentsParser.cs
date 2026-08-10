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
            string.IsNullOrWhiteSpace(arguments[1]) ||
            arguments[1].Length > 32_767 ||
            arguments[1].Any(char.IsControl) ||
            !Path.IsPathFullyQualified(arguments[1]) ||
            !Path.GetExtension(arguments[1]).Equals(".replica", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            snapshotPath = Path.GetFullPath(arguments[1]);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
