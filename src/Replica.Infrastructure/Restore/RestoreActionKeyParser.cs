using Replica.Core.Execution;
using Replica.Core.Planning;

namespace Replica.Infrastructure.Restore;

internal static class RestoreActionKeyParser
{
    public static string GetEnvironmentVariableName(
        RestoreAction action,
        EnvironmentVariableScope scope)
    {
        ArgumentNullException.ThrowIfNull(action);
        string expectedPrefix = $"{scope}:";
        string key = action.SourceDiffKey;
        if (key.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            key = key[expectedPrefix.Length..];
        }

        if (string.IsNullOrWhiteSpace(key) || key.Contains(':', StringComparison.Ordinal) ||
            key.Length > 32767 || key.Any(char.IsControl))
        {
            throw new ArgumentException("The environment variable action key is invalid.", nameof(action));
        }

        return key;
    }
}
