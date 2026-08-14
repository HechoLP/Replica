using System.Collections;
using Replica.Core.Platforms;

namespace Replica.Mac.Infrastructure.Scanning;

public sealed class MacEnvironmentSource : IMacEnvironmentSource
{
    public Task<MacEnvironmentResult> ReadAsync(CancellationToken cancellationToken)
    {
        return Task.Run(() => Read(cancellationToken), cancellationToken);
    }

    private static MacEnvironmentResult Read(CancellationToken cancellationToken)
    {
        List<PlatformEnvironmentVariable> variables = [];
        string? pathValue = null;
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = entry.Key?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            string value = entry.Value?.ToString() ?? string.Empty;
            bool sensitive = SensitiveEnvironmentPolicy.IsSensitive(name);
            bool canCaptureValue = SensitiveEnvironmentPolicy.CanCaptureValue(name);
            variables.Add(new PlatformEnvironmentVariable(
                name,
                canCaptureValue ? value : null,
                !canCaptureValue,
                sensitive ? "SensitiveName" : canCaptureValue ? null : "UnapprovedEnvironmentValue"));
            if (name.Equals("PATH", StringComparison.OrdinalIgnoreCase))
            {
                pathValue = value;
            }
        }

        List<PlatformPathEntry> paths = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        string[] entries = (pathValue ?? string.Empty).Split(
            Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int index = 0; index < entries.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = entries[index];
            bool duplicate = !seen.Add(path);
            paths.Add(new PlatformPathEntry(path, index, duplicate, Directory.Exists(path)));
        }

        return new MacEnvironmentResult(
            variables.OrderBy(variable => variable.Name, StringComparer.Ordinal).ToArray(),
            paths);
    }
}
