using System.Text.Json;
using System.Text.Json.Nodes;
using Replica.Core.Plugins;
using static Replica.Plugins.BuiltIn.DeveloperPluginHelpers;

namespace Replica.Plugins.BuiltIn;

public sealed class VisualStudioCodePlugin : BuiltInDeveloperPluginBase
{
    private static readonly IReadOnlyDictionary<string, Func<JsonElement, bool>> SupportedSettings =
        new Dictionary<string, Func<JsonElement, bool>>(StringComparer.Ordinal)
        {
            ["editor.fontSize"] = value => IsJsonIntegerInRange(value, 6, 100),
            ["editor.tabSize"] = value => IsJsonIntegerInRange(value, 1, 32),
            ["editor.insertSpaces"] = IsJsonBoolean,
            ["editor.minimap.enabled"] = IsJsonBoolean,
            ["files.autoSaveDelay"] = value => IsJsonIntegerInRange(value, 0, 600_000),
            ["window.zoomLevel"] = value => IsJsonIntegerInRange(value, -20, 20),
            ["workbench.colorTheme"] = IsSafePreferenceToken,
            ["workbench.iconTheme"] = IsSafePreferenceToken,
            ["terminal.integrated.defaultProfile.windows"] = IsSafePreferenceToken,
        };

    public override string Id => "replica.developer.vscode";

    public override string DisplayName => "Visual Studio Code";

    protected override DeveloperToolQuery? DetectionQuery => DeveloperToolQuery.VisualStudioCodeVersion;

    public override async Task<IReadOnlyList<PluginRestoreAction>> BuildRestoreActionsAsync(
        PluginComparisonResult comparison,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<PluginRestoreAction> baseActions = await base.BuildRestoreActionsAsync(
            comparison,
            cancellationToken).ConfigureAwait(false);
        bool installNeeded = comparison.Differences.Any(difference =>
            difference.Key.Equals("vscode.version", StringComparison.OrdinalIgnoreCase) &&
            difference.SourceValue is not null &&
            difference.TargetValue is null);
        if (!installNeeded)
        {
            return baseActions;
        }

        string installId = $"{Id}:install";
        List<PluginRestoreAction> actions =
        [
            new PluginRestoreAction(
                installId,
                Replica.Core.Planning.RestoreActionType.InstallPackage,
                "Install Visual Studio Code",
                "Install the allow-listed Microsoft.VisualStudioCode winget package after restore-plan review.",
                "Microsoft.VisualStudioCode",
                Replica.Core.Diffing.DiffRiskLevel.Medium,
                false,
                [],
                [PluginRestoreStrategy.Install, PluginRestoreStrategy.Validate]),
        ];
        actions.AddRange(baseActions
            .Where(action => !action.Name.EndsWith("vscode.version", StringComparison.OrdinalIgnoreCase))
            .Select(action => action with
            {
                Dependencies = action.Dependencies.Append(installId).Distinct(StringComparer.Ordinal).ToArray(),
            }));
        return actions;
    }

    public override async Task<PluginSnapshot> CaptureAsync(
        PluginCaptureContext context,
        CancellationToken cancellationToken)
    {
        string userData = Path.Combine(
            context.Host.GetKnownPath(DeveloperKnownPath.RoamingApplicationData),
            "Code",
            "User");
        List<PluginCapturedFile> files = [];
        PluginCapturedFile? settings = await CaptureProjectedJsonFileAsync(
            context.Host,
            Path.Combine(userData, "settings.json"),
            "settings.json",
            SupportedSettings,
            cancellationToken).ConfigureAwait(false);
        if (settings is not null)
        {
            files.Add(settings);
        }

        DeveloperToolQueryResult version = await context.Host.QueryAsync(
            DeveloperToolQuery.VisualStudioCodeVersion,
            cancellationToken).ConfigureAwait(false);
        DeveloperToolQueryResult extensions = await context.Host.QueryAsync(
            DeveloperToolQuery.VisualStudioCodeExtensions,
            cancellationToken).ConfigureAwait(false);
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        AddVersion(values, "vscode.version", version);
        foreach (string extension in Lines(extensions.StandardOutput))
        {
            string[] parts = extension.Split('@', 2);
            if (parts.Length == 2 && IsPackageName(parts[0]))
            {
                values[$"extension:{parts[0].ToLowerInvariant()}"] = parts[1];
            }
        }

        IReadOnlyList<PluginSensitiveExclusion> exclusions = await GetSensitiveExclusionsAsync(
            context,
            cancellationToken).ConfigureAwait(false);
        return Snapshot(values, files, exclusions, Warnings(version, extensions));
    }

    public override Task<IReadOnlyList<PluginSensitiveExclusion>> GetSensitiveExclusionsAsync(
        PluginCaptureContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<PluginSensitiveExclusion>>(
        [
            Exclusion("User/globalStorage", "VSCodeAuthentication", "Login and extension authentication state is excluded."),
            Exclusion("User/workspaceStorage", "WorkspaceSensitiveData", "Workspace storage may contain credentials and private history."),
            Exclusion("User/History", "EditorHistory", "Editor history is never captured."),
            Exclusion("User/keybindings.json", "ArbitraryCode", "Keybinding commands and arguments are not captured."),
            Exclusion("User/tasks.json", "ArbitraryCode", "Task commands and arguments are not captured."),
            Exclusion("User/snippets", "ArbitraryCode", "Snippet bodies may contain credentials and are inventory-only, not captured."),
            Exclusion("unrecognized settings.json keys", "UnknownSchema", "Only explicitly supported preference keys and value types are captured."),
            Exclusion("Cache", "Cache", "VS Code caches are never captured."),
            Exclusion("GitHub authentication", "Credential", "GitHub and Remote Tunnel credentials are never captured."),
        ]);
    }
}

public sealed class GitPlugin : BuiltInDeveloperPluginBase
{
    public override string Id => "replica.developer.git";

    public override string DisplayName => "Git";

    protected override DeveloperToolQuery? DetectionQuery => DeveloperToolQuery.GitVersion;

    public override async Task<PluginSnapshot> CaptureAsync(
        PluginCaptureContext context,
        CancellationToken cancellationToken)
    {
        DeveloperToolQueryResult version = await context.Host.QueryAsync(
            DeveloperToolQuery.GitVersion,
            cancellationToken).ConfigureAwait(false);
        DeveloperToolQueryResult configuration = await context.Host.QueryAsync(
            DeveloperToolQuery.GitConfiguration,
            cancellationToken).ConfigureAwait(false);
        Dictionary<string, string> values = new(
            ParseAllowListedConfiguration(configuration.StandardOutput, IsAllowedGitKey),
            StringComparer.OrdinalIgnoreCase);
        AddVersion(values, "git.version", version);

        if (context.IncludeSshPublicKeyFingerprints)
        {
            string ssh = Path.Combine(
                context.Host.GetKnownPath(DeveloperKnownPath.UserProfile),
                ".ssh");
            foreach (string file in context.Host.EnumerateFiles(ssh, "*.pub", recursive: false))
            {
                string? publicKey = await context.Host.ReadTextFileAsync(file, cancellationToken)
                    .ConfigureAwait(false);
                if (publicKey is not null)
                {
                    values[$"ssh-public-key-fingerprint:{Path.GetFileName(file)}"] =
                        HashFingerprint(publicKey.Trim());
                }
            }
        }

        IReadOnlyList<PluginSensitiveExclusion> exclusions = await GetSensitiveExclusionsAsync(
            context,
            cancellationToken).ConfigureAwait(false);
        return Snapshot(values, [], exclusions, Warnings(version, configuration));
    }

    public override Task<IReadOnlyList<PluginSensitiveExclusion>> GetSensitiveExclusionsAsync(
        PluginCaptureContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<PluginSensitiveExclusion>>(
        [
            Exclusion("credential values", "Credential", "Only the credential.helper name is captured."),
            Exclusion("OAuth tokens", "OAuthToken", "OAuth tokens are never queried or captured."),
            Exclusion("~/.ssh private keys", "SshPrivateKey", "Private keys are never read; public-key fingerprints are opt-in."),
        ]);
    }

    private static bool IsAllowedGitKey(string key)
    {
        return key.Equals("user.name", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("user.email", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("init.defaultbranch", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("core.editor", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("credential.helper", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class PowerShellPlugin : BuiltInDeveloperPluginBase
{
    public override string Id => "replica.developer.powershell";

    public override string DisplayName => "PowerShell";

    protected override DeveloperToolQuery? DetectionQuery => DeveloperToolQuery.PowerShellVersion;

    public override async Task<PluginSnapshot> CaptureAsync(
        PluginCaptureContext context,
        CancellationToken cancellationToken)
    {
        DeveloperToolQueryResult version = await context.Host.QueryAsync(
            DeveloperToolQuery.PowerShellVersion,
            cancellationToken).ConfigureAwait(false);
        DeveloperToolQueryResult modules = await context.Host.QueryAsync(
            DeveloperToolQuery.PowerShellModules,
            cancellationToken).ConfigureAwait(false);
        Dictionary<string, string> values = [];
        AddVersion(values, "powershell.version", version);
        foreach ((string name, string moduleVersion) in ParseNameVersionJson(modules.StandardOutput))
        {
            values[$"powershell-module:{name}"] = moduleVersion;
        }

        IReadOnlyList<PluginSensitiveExclusion> exclusions = await GetSensitiveExclusionsAsync(
            context,
            cancellationToken).ConfigureAwait(false);
        return Snapshot(values, [], exclusions, Warnings(version, modules));
    }

    public override Task<IReadOnlyList<PluginSensitiveExclusion>> GetSensitiveExclusionsAsync(
        PluginCaptureContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<PluginSensitiveExclusion>>(
        [
            Exclusion("repository registration", "UntrustedRepository", "Restore never registers an untrusted repository."),
            Exclusion("ExecutionPolicy", "ExecutionPolicy", "Restore never changes PowerShell execution policy."),
            Exclusion("PowerShell profiles", "ArbitraryCode", "Profile script bodies may contain credentials or executable code and are never captured."),
        ]);
    }
}

public sealed class WindowsTerminalPlugin : BuiltInDeveloperPluginBase
{
    private static readonly IReadOnlyDictionary<string, Func<JsonElement, bool>> SupportedSettings =
        new Dictionary<string, Func<JsonElement, bool>>(StringComparer.Ordinal)
        {
            ["defaultProfile"] = IsJsonGuid,
            ["copyOnSelect"] = IsJsonBoolean,
            ["trimBlockSelection"] = IsJsonBoolean,
            ["snapToGridOnResize"] = IsJsonBoolean,
            ["alwaysShowTabs"] = IsJsonBoolean,
            ["showTabsInTitlebar"] = IsJsonBoolean,
            ["confirmCloseAllTabs"] = IsJsonBoolean,
            ["theme"] = IsSafePreferenceToken,
        };

    public override string Id => "replica.developer.windows-terminal";

    public override string DisplayName => "Windows Terminal";

    protected override DeveloperToolQuery? DetectionQuery => DeveloperToolQuery.PowerShellVersion;

    public override async Task<PluginDetectionResult> DetectAsync(
        PluginCaptureContext context,
        CancellationToken cancellationToken)
    {
        string? settings = FindSettings(context.Host);
        cancellationToken.ThrowIfCancellationRequested();
        return new PluginDetectionResult(settings is not null, null, []);
    }

    public override async Task<PluginSnapshot> CaptureAsync(
        PluginCaptureContext context,
        CancellationToken cancellationToken)
    {
        List<PluginCapturedFile> files = [];
        List<string> warnings = [];
        string? settingsPath = FindSettings(context.Host);
        if (settingsPath is not null)
        {
            string? content = await context.Host.ReadTextFileAsync(settingsPath, cancellationToken)
                .ConfigureAwait(false);
            if (content is not null)
            {
                AnalyzeTerminalSettings(content, context.Host, warnings);
                string? projected = ProjectAllowListedJson(content, SupportedSettings);
                if (projected is null)
                {
                    warnings.Add("Windows Terminal settings could not be projected safely.");
                }
                else
                {
                    files.Add(new PluginCapturedFile("settings.json", projected, "application/json"));
                }
            }
        }

        IReadOnlyList<PluginSensitiveExclusion> exclusions = await GetSensitiveExclusionsAsync(
            context,
            cancellationToken).ConfigureAwait(false);
        return Snapshot(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            files,
            exclusions,
            warnings);
    }

    public override Task<IReadOnlyList<PluginSensitiveExclusion>> GetSensitiveExclusionsAsync(
        PluginCaptureContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<PluginSensitiveExclusion>>(
        [
            Exclusion("state and cache", "Cache", "Terminal runtime state and cache are not captured."),
            Exclusion("profiles, actions, and color schemes", "ArbitraryCommand", "Command lines and open-ended profile content are reviewed manually and are not captured."),
            Exclusion("unrecognized settings.json keys", "UnknownSchema", "Only explicitly supported preference keys and value types are captured."),
        ]);
    }

    private static string? FindSettings(IDeveloperPluginHost host)
    {
        string packages = Path.Combine(
            host.GetKnownPath(DeveloperKnownPath.LocalApplicationData),
            "Packages");
        foreach (string family in new[]
                 {
                     "Microsoft.WindowsTerminal_8wekyb3d8bbwe",
                     "Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe",
                 })
        {
            string path = Path.Combine(packages, family, "LocalState", "settings.json");
            if (host.FileExists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static void AnalyzeTerminalSettings(
        string content,
        IDeveloperPluginHost host,
        ICollection<string> warnings)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(content);
            if (!document.RootElement.TryGetProperty("profiles", out JsonElement profiles) ||
                !profiles.TryGetProperty("list", out JsonElement list) ||
                list.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            HashSet<string> guids = new(StringComparer.OrdinalIgnoreCase);
            foreach (JsonElement profile in list.EnumerateArray())
            {
                if (profile.TryGetProperty("guid", out JsonElement guid) &&
                    !guids.Add(guid.GetString() ?? string.Empty))
                {
                    warnings.Add("A Windows Terminal profile GUID conflict requires user review.");
                }

                string? command = profile.TryGetProperty("commandline", out JsonElement commandLine)
                    ? commandLine.GetString()
                    : null;
                if (!string.IsNullOrWhiteSpace(command) &&
                    Path.IsPathRooted(command) &&
                    !host.FileExists(command))
                {
                    warnings.Add("A Windows Terminal profile references a shell that is not installed.");
                }
            }
        }
        catch (JsonException)
        {
            warnings.Add("Windows Terminal settings could not be parsed.");
        }
    }
}

public sealed class NodeJsPlugin : BuiltInDeveloperPluginBase
{
    public override string Id => "replica.developer.nodejs";

    public override string DisplayName => "Node.js";

    protected override DeveloperToolQuery? DetectionQuery => DeveloperToolQuery.NodeVersion;

    public override async Task<PluginSnapshot> CaptureAsync(
        PluginCaptureContext context,
        CancellationToken cancellationToken)
    {
        DeveloperToolQueryResult node = await context.Host.QueryAsync(
            DeveloperToolQuery.NodeVersion,
            cancellationToken).ConfigureAwait(false);
        DeveloperToolQueryResult npm = await context.Host.QueryAsync(
            DeveloperToolQuery.NpmVersion,
            cancellationToken).ConfigureAwait(false);
        DeveloperToolQueryResult packages = await context.Host.QueryAsync(
            DeveloperToolQuery.NpmGlobalPackages,
            cancellationToken).ConfigureAwait(false);
        Dictionary<string, string> values = [];
        AddVersion(values, "node.version", node);
        AddVersion(values, "npm.version", npm);
        foreach ((string name, string packageVersion) in ParseNpmPackages(packages.StandardOutput))
        {
            values[$"npm-global-package:{name}"] = packageVersion;
        }

        string user = context.Host.GetKnownPath(DeveloperKnownPath.UserProfile);
        if (context.Host.DirectoryExists(Path.Combine(user, ".nvm")))
        {
            values["node.version-manager"] = "nvm";
        }
        else if (context.Host.DirectoryExists(Path.Combine(user, ".fnm")))
        {
            values["node.version-manager"] = "fnm";
        }
        else if (context.Host.DirectoryExists(Path.Combine(user, ".volta")))
        {
            values["node.version-manager"] = "volta";
        }

        IReadOnlyList<PluginSensitiveExclusion> exclusions = await GetSensitiveExclusionsAsync(
            context,
            cancellationToken).ConfigureAwait(false);
        return Snapshot(values, [], exclusions, Warnings(node, npm, packages));
    }

    public override Task<IReadOnlyList<PluginSensitiveExclusion>> GetSensitiveExclusionsAsync(
        PluginCaptureContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<PluginSensitiveExclusion>>(
        [
            Exclusion("~/.npmrc", "NpmAuthentication", "npm authentication values and tokens are never read."),
            Exclusion("npm cache", "Cache", "npm cache content is never captured."),
        ]);
    }
}

public sealed class PythonPlugin : BuiltInDeveloperPluginBase
{
    public override string Id => "replica.developer.python";

    public override string DisplayName => "Python";

    protected override DeveloperToolQuery? DetectionQuery => DeveloperToolQuery.PythonInterpreters;

    public override async Task<PluginSnapshot> CaptureAsync(
        PluginCaptureContext context,
        CancellationToken cancellationToken)
    {
        DeveloperToolQueryResult interpreters = await context.Host.QueryAsync(
            DeveloperToolQuery.PythonInterpreters,
            cancellationToken).ConfigureAwait(false);
        DeveloperToolQueryResult packages = await context.Host.QueryAsync(
            DeveloperToolQuery.PythonPackages,
            cancellationToken).ConfigureAwait(false);
        Dictionary<string, string> values = [];
        int interpreterIndex = 0;
        foreach (string interpreter in Lines(interpreters.StandardOutput))
        {
            values[$"python.interpreter:{interpreterIndex++}"] = interpreter;
        }

        foreach ((string interpreter, string name, string packageVersion) in
                 ParsePythonPackageManifests(packages.StandardOutput))
        {
            string interpreterId = HashFingerprint(interpreter)[..16];
            values[$"python-package:{interpreterId}:{name}"] = packageVersion;
        }

        string user = context.Host.GetKnownPath(DeveloperKnownPath.UserProfile);
        foreach (string rootName in new[] { ".virtualenvs", "Envs" })
        {
            string root = Path.Combine(user, rootName);
            foreach (string configuration in context.Host.EnumerateFiles(root, "pyvenv.cfg", recursive: true))
            {
                string relative = Path.GetRelativePath(user, Path.GetDirectoryName(configuration)!);
                values[$"python-venv:{relative.Replace('\\', '/')}"] = "metadata-only";
            }
        }

        IReadOnlyList<PluginSensitiveExclusion> exclusions = await GetSensitiveExclusionsAsync(
            context,
            cancellationToken).ConfigureAwait(false);
        return Snapshot(values, [], exclusions, Warnings(interpreters, packages));
    }

    public override Task<IReadOnlyList<PluginSensitiveExclusion>> GetSensitiveExclusionsAsync(
        PluginCaptureContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<PluginSensitiveExclusion>>(
        [
            Exclusion("virtual environment contents", "VenvContent", "Only virtual-environment names and locations are recorded by default."),
            Exclusion("pip credentials", "PackageCredential", "Package index credentials are never read."),
        ]);
    }
}

internal static class DeveloperPluginHelpers
{
    public static PluginSensitiveExclusion Exclusion(
        string path,
        string reason,
        string description) => new(path, reason, description);

    public static IEnumerable<string> Lines(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static bool IsPackageName(string value) =>
        value.Length is > 0 and <= 255 &&
        value.All(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_');

    public static void AddVersion(
        IDictionary<string, string> values,
        string key,
        DeveloperToolQueryResult query)
    {
        string? version = query.Version ?? Lines(query.StandardOutput).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(version))
        {
            values[key] = version.Trim();
        }
    }

    public static IReadOnlyList<string> Warnings(params DeveloperToolQueryResult[] results) =>
        results.SelectMany(result => result.Warnings ?? [])
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    public static IEnumerable<(string Name, string Version)> ParseNameVersionJson(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(value);
        }
        catch (JsonException)
        {
            yield break;
        }

        IEnumerable<JsonNode?> nodes = root is JsonArray array ? array : [root];
        foreach (JsonNode? node in nodes)
        {
            string? name = node?["Name"]?.GetValue<string>() ?? node?["name"]?.GetValue<string>();
            string? version = node?["Version"]?.ToString() ?? node?["version"]?.ToString();
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(version))
            {
                yield return (name, version);
            }
        }
    }

    public static IEnumerable<(string Name, string Version)> ParseNpmPackages(string value)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(value);
        }
        catch (JsonException)
        {
            yield break;
        }

        if (root?["dependencies"] is not JsonObject dependencies)
        {
            yield break;
        }

        foreach ((string name, JsonNode? node) in dependencies)
        {
            string? version = node?["version"]?.GetValue<string>();
            if (!BuiltInDeveloperPluginBase.IsSensitiveKey(name) && !string.IsNullOrWhiteSpace(version))
            {
                yield return (name, version);
            }
        }
    }

    public static IEnumerable<(string Interpreter, string Name, string Version)>
        ParsePythonPackageManifests(string value)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(value);
        }
        catch (JsonException)
        {
            yield break;
        }

        IEnumerable<JsonNode?> manifests = root is JsonArray array ? array : [root];
        foreach (JsonNode? manifest in manifests)
        {
            string? interpreter = manifest?["Interpreter"]?.GetValue<string>() ??
                manifest?["interpreter"]?.GetValue<string>();
            JsonArray? packages = manifest?["Packages"] as JsonArray ??
                manifest?["packages"] as JsonArray;
            if (string.IsNullOrWhiteSpace(interpreter) || packages is null)
            {
                continue;
            }

            foreach (JsonNode? package in packages)
            {
                string? name = package?["name"]?.GetValue<string>() ??
                    package?["Name"]?.GetValue<string>();
                string? version = package?["version"]?.ToString() ??
                    package?["Version"]?.ToString();
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(version))
                {
                    yield return (interpreter, name, version);
                }
            }
        }
    }
}
