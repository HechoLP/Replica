using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Replica.Core.Plugins;

namespace Replica.Plugins.BuiltIn;

public abstract class BuiltInApplicationPluginBase : BuiltInDeveloperPluginBase
{
    protected abstract BuiltInApplication Application { get; }

    protected abstract string? PackageIdentifier { get; }

    protected abstract bool IsSupportedVersion(string version);

    public override Task<PluginDetectionResult> DetectAsync(
        PluginCaptureContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        BuiltInApplicationInfo info = context.Host.GetApplicationInfo(Application);
        List<string> warnings = [];
        if (info.IsInstalled &&
            (string.IsNullOrWhiteSpace(info.Version) || !IsSupportedVersion(info.Version)))
        {
            warnings.Add("UnsupportedVersion");
        }

        return Task.FromResult(new PluginDetectionResult(
            info.IsInstalled,
            info.Version,
            warnings));
    }

    protected static Dictionary<string, string> ApplicationValues(BuiltInApplicationInfo info)
    {
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase)
        {
            ["application.installed"] = info.IsInstalled.ToString(),
        };
        if (!string.IsNullOrWhiteSpace(info.Version))
        {
            values["application.version"] = info.Version;
        }

        return values;
    }

    public override async Task<IReadOnlyList<PluginRestoreAction>> BuildRestoreActionsAsync(
        PluginComparisonResult comparison,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<PluginRestoreAction> baseActions = await base.BuildRestoreActionsAsync(
            comparison,
            cancellationToken).ConfigureAwait(false);
        bool installNeeded = comparison.Differences.Any(difference =>
            difference.Key.Equals("application.installed", StringComparison.OrdinalIgnoreCase) &&
            difference.SourceValue?.Equals("True", StringComparison.OrdinalIgnoreCase) == true &&
            difference.TargetValue?.Equals("False", StringComparison.OrdinalIgnoreCase) == true);
        bool unsupportedTarget = comparison.Warnings?.Contains(
            "UnsupportedVersion",
            StringComparer.Ordinal) == true;
        if (!installNeeded && !unsupportedTarget)
        {
            return baseActions;
        }

        if (unsupportedTarget)
        {
            return baseActions.Select(action => action with
            {
                Type = Replica.Core.Planning.RestoreActionType.ManualInstruction,
                Description = "The installed application version is unsupported; review this setting manually.",
                IsManualOnly = true,
                SupportedStrategies = [PluginRestoreStrategy.KeepCurrent, PluginRestoreStrategy.Validate],
            }).ToArray();
        }

        string installId = $"{Id}:install";
        bool manualInstall = PackageIdentifier is null;
        PluginRestoreAction install = new(
            installId,
            manualInstall
                ? Replica.Core.Planning.RestoreActionType.ManualInstruction
                : Replica.Core.Planning.RestoreActionType.InstallPackage,
            $"Install {DisplayName}",
            manualInstall
                ? "Install the licensed application from its official source, then rescan before restoring settings."
                : "Install the allow-listed winget package after restore-plan review.",
            PackageIdentifier,
            Replica.Core.Diffing.DiffRiskLevel.Medium,
            manualInstall,
            [],
            manualInstall
                ? [PluginRestoreStrategy.KeepCurrent, PluginRestoreStrategy.Validate]
                : [PluginRestoreStrategy.Install, PluginRestoreStrategy.Validate]);
        if (manualInstall)
        {
            return [install, .. baseActions
                .Where(action => !IsApplicationMetadataAction(action))
                .Select(action => action with
                {
                    Type = Replica.Core.Planning.RestoreActionType.ManualInstruction,
                    Description = "Install the application first, then create a new reviewed restore plan.",
                    IsManualOnly = true,
                    SupportedStrategies = [PluginRestoreStrategy.KeepCurrent, PluginRestoreStrategy.Validate],
                })];
        }

        return [install, .. baseActions
            .Where(action => !IsApplicationMetadataAction(action))
            .Select(action => action with
            {
                Dependencies = action.Dependencies.Append(installId)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
            })];
    }

    protected List<string> ApplicationWarnings(BuiltInApplicationInfo info)
    {
        List<string> warnings = [];
        if (info.IsRunning)
        {
            warnings.Add("ApplicationRunning");
        }

        if (info.IsInstalled &&
            (string.IsNullOrWhiteSpace(info.Version) || !IsSupportedVersion(info.Version)))
        {
            warnings.Add("UnsupportedVersion");
        }

        return warnings;
    }

    protected static async Task AddJsonFileAsync(
        PluginCaptureContext context,
        string physicalPath,
        string logicalPath,
        ICollection<PluginCapturedFile> files,
        ICollection<string> warnings,
        bool redactUrlQueries,
        CancellationToken cancellationToken)
    {
        bool exists = context.Host.FileExists(physicalPath);
        PluginCapturedFile? captured = await CaptureFileAsync(
            context.Host,
            physicalPath,
            logicalPath,
            sanitizeJson: true,
            cancellationToken).ConfigureAwait(false);
        if (captured is null)
        {
            if (exists)
            {
                warnings.Add($"InvalidSettingsFile:{logicalPath.Replace('\\', '/')}");
            }

            return;
        }

        if (redactUrlQueries)
        {
            captured = captured with { Content = RedactUrlQueries(captured.Content) };
        }

        files.Add(captured);
    }

    protected static async Task AddTextFileAsync(
        PluginCaptureContext context,
        string physicalPath,
        string logicalPath,
        ICollection<PluginCapturedFile> files,
        CancellationToken cancellationToken)
    {
        PluginCapturedFile? captured = await CaptureFileAsync(
            context.Host,
            physicalPath,
            logicalPath,
            sanitizeJson: false,
            cancellationToken).ConfigureAwait(false);
        if (captured is not null)
        {
            files.Add(captured with
            {
                Content = RedactTextUrlQueries(SanitizeTextContent(captured.Content)),
            });
        }
    }

    protected static bool VersionMajorIs(string version, params int[] supportedMajors)
    {
        string digits = new(version
            .SkipWhile(character => !char.IsDigit(character))
            .TakeWhile(character => char.IsDigit(character))
            .ToArray());
        return int.TryParse(digits, out int major) && supportedMajors.Contains(major);
    }

    protected static bool VersionAtLeast(string version, int minimumMajor, int minimumMinor = 0)
    {
        string normalized = new(version
            .SkipWhile(character => !char.IsDigit(character))
            .TakeWhile(character => char.IsDigit(character) || character == '.')
            .ToArray());
        string[] parts = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !int.TryParse(parts[0], out int major))
        {
            return false;
        }

        int minor = parts.Length > 1 && int.TryParse(parts[1], out int parsedMinor)
            ? parsedMinor
            : 0;
        return major > minimumMajor || major == minimumMajor && minor >= minimumMinor;
    }

    protected static string RelativeLogicalPath(string root, string path)
    {
        return Path.GetRelativePath(root, path).Replace('\\', '/');
    }

    protected static bool IsPathUnder(string candidate, string root)
    {
        try
        {
            string normalizedRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            string normalizedCandidate = Path.GetFullPath(candidate);
            return normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string RedactUrlQueries(string json)
    {
        try
        {
            JsonNode? root = JsonNode.Parse(json);
            if (root is null)
            {
                return json;
            }

            RedactNode(root);
            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static string RedactTextUrlQueries(string content)
    {
        return Regex.Replace(
            content,
            @"https?://[^\s\""'<>]+",
            static match =>
            {
                if (!Uri.TryCreate(match.Value, UriKind.Absolute, out Uri? uri) ||
                    string.IsNullOrEmpty(uri.Query))
                {
                    return match.Value;
                }

                return new UriBuilder(uri) { Query = string.Empty }.Uri.ToString();
            },
            RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(100));
    }

    private static void RedactNode(JsonNode node)
    {
        if (node is JsonObject jsonObject)
        {
            foreach ((string key, JsonNode? child) in jsonObject.ToArray())
            {
                if (child is JsonValue value &&
                    value.TryGetValue(out string? text) &&
                    Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) &&
                    (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
                    !string.IsNullOrEmpty(uri.Query))
                {
                    jsonObject[key] = new UriBuilder(uri) { Query = string.Empty }.Uri.ToString();
                }
                else if (child is not null)
                {
                    RedactNode(child);
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (JsonNode? child in array)
            {
                if (child is not null)
                {
                    RedactNode(child);
                }
            }
        }
    }

    private static bool IsApplicationMetadataAction(PluginRestoreAction action)
    {
        return action.Name.EndsWith("application.installed", StringComparison.OrdinalIgnoreCase) ||
            action.Name.EndsWith("application.version", StringComparison.OrdinalIgnoreCase);
    }
}
