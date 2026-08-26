using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Replica.Core.Diffing;
using Replica.Core.Planning;
using Replica.Core.Plugins;

namespace Replica.Plugins.BuiltIn;

public abstract class BuiltInDeveloperPluginBase : IBuiltInPlugin
{
    private static readonly string[] SensitiveKeyParts =
    [
        "token",
        "password",
        "passwd",
        "pwd",
        "secret",
        "credential",
        "authorization",
        "oauth",
        "bearer",
        "cookie",
        "apikey",
        "accesskey",
        "clientkey",
        "signingkey",
        "streamkey",
        "encryptionkey",
        "decryptionkey",
        "license",
        "serialnumber",
        "activationcode",
        "connectionstring",
        "privatekey",
    ];
    private static readonly Regex ExternalUrlPattern = new(
        @"https?://[^\s\""'<>]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex CredentialAssignmentPattern = new(
        @"(?ix)(?:api[_\-. ]?key|access[_\-. ]?key|client[_\-. ]?secret|private[_\-. ]?key|password|passwd|pwd|token|authorization|cookie|connection[_\-. ]?string)\s*[:=]\s*[^\s,;]+",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public abstract string Id { get; }

    public abstract string DisplayName { get; }

    public virtual string Version => "1.0.0";

    protected virtual DeveloperToolQuery? DetectionQuery => null;

    public virtual async Task<PluginDetectionResult> DetectAsync(
        PluginCaptureContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (DetectionQuery is not DeveloperToolQuery query)
        {
            return new PluginDetectionResult(false, null, ["No detection query is configured."]);
        }

        DeveloperToolQueryResult result = await context.Host
            .QueryAsync(query, cancellationToken)
            .ConfigureAwait(false);
        return new PluginDetectionResult(
            result.IsAvailable,
            result.Version,
            result.Warnings ?? []);
    }

    public abstract Task<PluginSnapshot> CaptureAsync(
        PluginCaptureContext context,
        CancellationToken cancellationToken);

    public virtual Task<PluginComparisonResult> CompareAsync(
        PluginSnapshot source,
        PluginSnapshot target,
        CancellationToken cancellationToken)
    {
        ValidatePluginSnapshot(source, nameof(source));
        ValidatePluginSnapshot(target, nameof(target));
        cancellationToken.ThrowIfCancellationRequested();

        List<PluginDifference> differences = [];
        string[] valueKeys = source.Values.Keys
            .Concat(target.Values.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (string key in valueKeys)
        {
            source.Values.TryGetValue(key, out string? sourceValue);
            target.Values.TryGetValue(key, out string? targetValue);
            AddDifference(differences, key, key, sourceValue, targetValue, false);
        }

        Dictionary<string, PluginCapturedFile> sourceFiles = source.Files.ToDictionary(
            file => file.LogicalPath,
            StringComparer.OrdinalIgnoreCase);
        Dictionary<string, PluginCapturedFile> targetFiles = target.Files.ToDictionary(
            file => file.LogicalPath,
            StringComparer.OrdinalIgnoreCase);
        string[] fileKeys = sourceFiles.Keys
            .Concat(targetFiles.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (string key in fileKeys)
        {
            sourceFiles.TryGetValue(key, out PluginCapturedFile? sourceFile);
            targetFiles.TryGetValue(key, out PluginCapturedFile? targetFile);
            bool semanticJson = key.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
            AddDifference(
                differences,
                key,
                key,
                NormalizeContent(sourceFile?.Content, semanticJson),
                NormalizeContent(targetFile?.Content, semanticJson),
                true);
        }

        return Task.FromResult(new PluginComparisonResult(Id, differences, target.Warnings));
    }

    public virtual Task<IReadOnlyList<PluginRestoreAction>> BuildRestoreActionsAsync(
        PluginComparisonResult comparison,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        if (!string.Equals(comparison.PluginId, Id, StringComparison.Ordinal))
        {
            throw new ArgumentException("The comparison belongs to another plugin.", nameof(comparison));
        }

        cancellationToken.ThrowIfCancellationRequested();
        List<PluginRestoreAction> actions = [];
        foreach (PluginDifference difference in comparison.Differences
                     .Where(item => item.Type != DiffType.ExactMatch)
                     .OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            RestoreActionType type = GetRestoreActionType(difference.Key);
            bool manual = !difference.CanRestoreAutomatically ||
                difference.Risk >= DiffRiskLevel.High;
            actions.Add(new PluginRestoreAction(
                $"{Id}:{StableId(difference.Key)}",
                manual ? RestoreActionType.ManualInstruction : type,
                $"Restore {difference.DisplayName}",
                manual
                    ? "Review and apply this developer setting manually."
                    : "Apply the allow-listed built-in plugin setting after restore-plan review.",
                difference.SourceValue,
                difference.Risk,
                manual,
                [],
                GetRestoreStrategies(type)));
        }

        return Task.FromResult<IReadOnlyList<PluginRestoreAction>>(actions);
    }

    public virtual async Task<PluginValidationResult> ValidateAsync(
        PluginSnapshot expected,
        PluginCaptureContext context,
        CancellationToken cancellationToken)
    {
        PluginSnapshot current = await CaptureAsync(context, cancellationToken).ConfigureAwait(false);
        PluginComparisonResult comparison = await CompareAsync(
            expected,
            current,
            cancellationToken).ConfigureAwait(false);
        string[] errors = comparison.Differences
            .Where(difference => difference.Type != DiffType.ExactMatch)
            .Select(difference => $"{difference.Key} does not match the approved snapshot.")
            .ToArray();
        return new PluginValidationResult(errors.Length == 0, errors, current.Warnings);
    }

    public abstract Task<IReadOnlyList<PluginSensitiveExclusion>> GetSensitiveExclusionsAsync(
        PluginCaptureContext context,
        CancellationToken cancellationToken);

    protected PluginSnapshot Snapshot(
        IReadOnlyDictionary<string, string> values,
        IReadOnlyList<PluginCapturedFile> files,
        IReadOnlyList<PluginSensitiveExclusion> exclusions,
        IReadOnlyList<string>? warnings = null)
    {
        return new PluginSnapshot(Id, Version, values, files, exclusions, warnings ?? []);
    }

    protected static async Task<PluginCapturedFile?> CaptureProjectedJsonFileAsync(
        IDeveloperPluginHost host,
        string physicalPath,
        string logicalPath,
        IReadOnlyDictionary<string, Func<JsonElement, bool>> allowedRootProperties,
        CancellationToken cancellationToken)
    {
        string? content = await host.ReadTextFileAsync(physicalPath, cancellationToken)
            .ConfigureAwait(false);
        if (content is null)
        {
            return null;
        }

        string? projected = ProjectAllowListedJson(content, allowedRootProperties);
        return projected is null
            ? null
            : new PluginCapturedFile(
                logicalPath.Replace('\\', '/'),
                projected,
                "application/json");
    }

    protected static string? ProjectAllowListedJson(
        string json,
        IReadOnlyDictionary<string, Func<JsonElement, bool>> allowedRootProperties)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 64,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            JsonObject projected = [];
            foreach ((string propertyName, Func<JsonElement, bool> validator) in allowedRootProperties)
            {
                if (!document.RootElement.TryGetProperty(propertyName, out JsonElement value) ||
                    !validator(value))
                {
                    continue;
                }

                projected[propertyName] = JsonNode.Parse(value.GetRawText());
            }

            return projected.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    protected static bool IsJsonBoolean(JsonElement value) =>
        value.ValueKind is JsonValueKind.True or JsonValueKind.False;

    protected static bool IsJsonIntegerInRange(JsonElement value, int minimum, int maximum) =>
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out int parsed) &&
        parsed >= minimum &&
        parsed <= maximum;

    protected static bool IsJsonGuid(JsonElement value) =>
        value.ValueKind == JsonValueKind.String &&
        Guid.TryParse(value.GetString(), out _);

    protected static bool IsSafePreferenceToken(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string? text = value.GetString();
        return text is not null &&
            text.Length is > 0 and <= 128 &&
            !IsSensitiveText(text) &&
            text.All(character => char.IsLetterOrDigit(character) ||
                character is ' ' or '.' or '-' or '_' or '#' or '{' or '}' or '%');
    }

    protected static IReadOnlyDictionary<string, string> ParseAllowListedConfiguration(
        string text,
        Func<string, bool> allowKey)
    {
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        foreach (string line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = line.IndexOf('=');
            if (separator < 1)
            {
                separator = line.IndexOf(' ');
            }

            if (separator < 1)
            {
                continue;
            }

            string key = line[..separator].Trim();
            string value = line[(separator + 1)..].Trim();
            bool safeCredentialHelperName = key.Equals(
                "credential.helper",
                StringComparison.OrdinalIgnoreCase) &&
                value.Length is > 0 and <= 64 &&
                value.All(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_');
            if (allowKey(key) &&
                (safeCredentialHelperName ||
                    !key.Equals("credential.helper", StringComparison.OrdinalIgnoreCase) &&
                    !IsSensitiveKey(key) &&
                    !IsSensitiveText(value)))
            {
                values[key] = SanitizeExternalUrls(value);
            }
        }

        return values;
    }

    protected internal static bool IsSensitiveKey(string key)
    {
        string normalized = new(key
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
        return SensitiveKeyParts.Any(part =>
            normalized.Contains(part, StringComparison.Ordinal));
    }

    protected static string SanitizeExternalUrls(string value) =>
        ExternalUrlPattern.Replace(value, static match =>
        {
            if (!Uri.TryCreate(match.Value, UriKind.Absolute, out Uri? uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return string.Empty;
            }

            UriBuilder safe = new(uri.Scheme, uri.Host, uri.IsDefaultPort ? -1 : uri.Port)
            {
                Path = "/",
                Query = string.Empty,
                Fragment = string.Empty,
                UserName = string.Empty,
                Password = string.Empty,
            };
            return safe.Uri.AbsoluteUri;
        });

    private static bool IsSensitiveText(string value)
    {
        if (IsSensitiveKey(value) || CredentialAssignmentPattern.IsMatch(value))
        {
            return true;
        }

        return ExternalUrlPattern.Matches(value).Any(match =>
            Uri.TryCreate(match.Value, UriKind.Absolute, out Uri? uri) &&
            (!string.IsNullOrEmpty(uri.UserInfo) ||
             !string.IsNullOrEmpty(uri.Query) ||
             !string.IsNullOrEmpty(uri.Fragment)));
    }

    protected static string HashFingerprint(string content)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    }

    protected virtual RestoreActionType GetRestoreActionType(string key)
    {
        if (key.StartsWith("extension:", StringComparison.OrdinalIgnoreCase))
        {
            return RestoreActionType.InstallExtension;
        }

        if (key.StartsWith("powershell-module:", StringComparison.OrdinalIgnoreCase))
        {
            return RestoreActionType.InstallPowerShellModule;
        }

        return key.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? RestoreActionType.MergeJson
            : RestoreActionType.RestoreFile;
    }

    protected virtual IReadOnlyList<PluginRestoreStrategy> GetRestoreStrategies(
        RestoreActionType actionType)
    {
        return actionType switch
        {
            RestoreActionType.InstallExtension or RestoreActionType.InstallPowerShellModule =>
                [PluginRestoreStrategy.Install, PluginRestoreStrategy.Validate],
            RestoreActionType.MergeJson =>
                [
                    PluginRestoreStrategy.Merge,
                    PluginRestoreStrategy.Replace,
                    PluginRestoreStrategy.KeepCurrent,
                    PluginRestoreStrategy.Validate,
                ],
            RestoreActionType.RestoreFile =>
                [
                    PluginRestoreStrategy.Replace,
                    PluginRestoreStrategy.KeepCurrent,
                    PluginRestoreStrategy.Validate,
                ],
            _ => [PluginRestoreStrategy.Validate],
        };
    }

    private static void AddDifference(
        ICollection<PluginDifference> differences,
        string key,
        string displayName,
        string? sourceValue,
        string? targetValue,
        bool file)
    {
        DiffType type = sourceValue is null
            ? DiffType.Extra
            : targetValue is null
                ? DiffType.Missing
                : string.Equals(sourceValue, targetValue, StringComparison.Ordinal)
                    ? DiffType.ExactMatch
                    : file
                        ? DiffType.FileChanged
                        : DiffType.ValueMismatch;
        differences.Add(new PluginDifference(
            key,
            displayName,
            sourceValue,
            targetValue,
            type,
            sourceValue is not null &&
                (file ||
                    key.StartsWith("extension:", StringComparison.OrdinalIgnoreCase) ||
                    key.StartsWith("powershell-module:", StringComparison.OrdinalIgnoreCase)),
            file ? DiffRiskLevel.Medium : DiffRiskLevel.Low,
            type.ToString()));
    }

    private static string? NormalizeContent(string? value, bool semanticJson)
    {
        if (!semanticJson || value is null)
        {
            return value;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(value, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            return JsonSerializer.Serialize(document.RootElement);
        }
        catch (JsonException)
        {
            return value;
        }
    }

    private void ValidatePluginSnapshot(PluginSnapshot snapshot, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(snapshot, parameterName);
        if (!string.Equals(snapshot.PluginId, Id, StringComparison.Ordinal))
        {
            throw new ArgumentException("The snapshot belongs to another plugin.", parameterName);
        }

        if (snapshot.Values is null || snapshot.Files is null || snapshot.Exclusions is null)
        {
            throw new ArgumentException("The plugin snapshot is incomplete.", parameterName);
        }
    }

    private static string StableId(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }
}
