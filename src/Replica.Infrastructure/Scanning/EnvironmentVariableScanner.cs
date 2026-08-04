using System.Collections;
using Replica.Core.Scanning;
using Replica.Core.Services;

namespace Replica.Infrastructure.Scanning;

public interface IEnvironmentValueSource
{
    Task<IReadOnlyDictionary<string, string>> ReadAsync(
        EnvironmentVariableTarget target,
        CancellationToken cancellationToken);
}

public sealed class WindowsEnvironmentValueSource : IEnvironmentValueSource
{
    public Task<IReadOnlyDictionary<string, string>> ReadAsync(
        EnvironmentVariableTarget target,
        CancellationToken cancellationToken)
    {
        return Task.Run<IReadOnlyDictionary<string, string>>(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                IDictionary values = System.Environment.GetEnvironmentVariables(target);
                Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
                foreach (DictionaryEntry entry in values)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.Key is string name && entry.Value is string value)
                    {
                        result[name] = value;
                    }
                }

                return result;
            },
            cancellationToken);
    }
}

public sealed class EnvironmentVariableScanner : IEnvironmentVariableScanner
{
    private static readonly string[] SensitiveMarkers =
    [
        "TOKEN",
        "SECRET",
        "PASSWORD",
        "KEY",
        "CREDENTIAL",
        "CONNECTION_STRING",
    ];

    private readonly IEnvironmentValueSource _source;

    public EnvironmentVariableScanner(IEnvironmentValueSource source)
    {
        _source = source;
    }

    public async Task<EnvironmentVariableScanResult> ScanAsync(
        CancellationToken cancellationToken)
    {
        List<ScannedEnvironmentVariable> variables = [];
        List<ScannedPathEntry> pathEntries = [];
        List<ScanWarning> warnings = [];
        HashSet<string> encounteredPaths = new(StringComparer.OrdinalIgnoreCase);
        int sensitiveCount = 0;

        foreach ((EnvironmentVariableTarget Target, string Scope) scope in new[]
                 {
                     (EnvironmentVariableTarget.Machine, "Machine"),
                     (EnvironmentVariableTarget.User, "User"),
                 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyDictionary<string, string> values;
            try
            {
                values = await _source.ReadAsync(scope.Target, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or System.Security.SecurityException)
            {
                warnings.Add(new ScanWarning(
                    "Environment",
                    $"{scope.Scope}EnvironmentUnavailable",
                    $"{scope.Scope} environment variables could not be read."));
                continue;
            }

            foreach ((string name, string value) in values.OrderBy(
                         item => item.Key,
                         StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (name.Equals("Path", StringComparison.OrdinalIgnoreCase))
                {
                    AddPathEntries(value, scope.Scope, encounteredPaths, pathEntries);
                    continue;
                }

                string? sensitiveMarker = FindSensitiveMarker(name);
                bool isSensitive = sensitiveMarker is not null;
                variables.Add(new ScannedEnvironmentVariable(
                    name,
                    isSensitive ? null : value,
                    scope.Scope,
                    isSensitive,
                    isSensitive ? $"SensitiveName:{sensitiveMarker}" : null));
                if (isSensitive)
                {
                    sensitiveCount++;
                }
            }
        }

        return new EnvironmentVariableScanResult(
            variables,
            pathEntries,
            sensitiveCount,
            warnings);
    }

    private static void AddPathEntries(
        string value,
        string scope,
        ISet<string> encounteredPaths,
        ICollection<ScannedPathEntry> entries)
    {
        string[] parts = value.Split(';');
        for (int index = 0; index < parts.Length; index++)
        {
            string path = parts[index].Trim();
            if (path.Length == 0)
            {
                continue;
            }

            string normalized = NormalizePath(path);
            bool duplicate = !encounteredPaths.Add(normalized);
            bool exists = false;
            try
            {
                exists = Directory.Exists(System.Environment.ExpandEnvironmentVariables(path));
            }
            catch (ArgumentException)
            {
                // Invalid values are preserved in their original order and marked missing.
            }

            entries.Add(new ScannedPathEntry(path, scope, index, duplicate, exists));
        }
    }

    private static string NormalizePath(string path)
    {
        string expanded = System.Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        return expanded.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string? FindSensitiveMarker(string name)
    {
        string normalized = name.ToUpperInvariant();
        return SensitiveMarkers.FirstOrDefault(marker =>
            normalized.Contains(marker, StringComparison.Ordinal));
    }
}
