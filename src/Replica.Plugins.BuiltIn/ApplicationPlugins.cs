using Replica.Core.Planning;
using Replica.Core.Plugins;
using static Replica.Plugins.BuiltIn.DeveloperPluginHelpers;

namespace Replica.Plugins.BuiltIn;

public sealed class PowerToysPlugin : BuiltInApplicationPluginBase
{
    private static readonly IReadOnlyDictionary<string, Func<System.Text.Json.JsonElement, bool>> GeneralSettings =
        new Dictionary<string, Func<System.Text.Json.JsonElement, bool>>(StringComparer.Ordinal)
        {
            ["startup"] = IsJsonBoolean,
            ["theme"] = IsSafePreferenceToken,
        };
    private static readonly IReadOnlyDictionary<string, Func<System.Text.Json.JsonElement, bool>> FancyZonesSettings =
        new Dictionary<string, Func<System.Text.Json.JsonElement, bool>>(StringComparer.Ordinal)
        {
            ["fancyzones_shiftDrag"] = IsJsonBoolean,
        };
    private static readonly IReadOnlyDictionary<string, Func<System.Text.Json.JsonElement, bool>> PowerRenameSettings =
        new Dictionary<string, Func<System.Text.Json.JsonElement, bool>>(StringComparer.Ordinal)
        {
            ["MRUEnabled"] = IsJsonBoolean,
        };
    private static readonly IReadOnlyDictionary<string, Func<System.Text.Json.JsonElement, bool>> AwakeSettings =
        new Dictionary<string, Func<System.Text.Json.JsonElement, bool>>(StringComparer.Ordinal)
        {
            ["mode"] = value => IsJsonIntegerInRange(value, 0, 10),
        };

    public override string Id => "replica.application.powertoys";

    public override string DisplayName => "Microsoft PowerToys";

    protected override BuiltInApplication Application => BuiltInApplication.PowerToys;

    protected override string? PackageIdentifier => "Microsoft.PowerToys";

    protected override bool IsSupportedVersion(string version) => VersionAtLeast(version, 0, 80);

    public override async Task<PluginSnapshot> CaptureAsync(
        PluginCaptureContext context,
        CancellationToken cancellationToken)
    {
        BuiltInApplicationInfo info = context.Host.GetApplicationInfo(Application);
        Dictionary<string, string> values = ApplicationValues(info);
        List<PluginCapturedFile> files = [];
        List<string> warnings = ApplicationWarnings(info);
        IReadOnlyList<PluginSensitiveExclusion> exclusions = await GetSensitiveExclusionsAsync(
            context,
            cancellationToken).ConfigureAwait(false);
        if (!info.IsInstalled)
        {
            return Snapshot(values, files, exclusions, warnings);
        }

        string root = Path.Combine(
            context.Host.GetKnownPath(DeveloperKnownPath.LocalApplicationData),
            "Microsoft",
            "PowerToys");
        (string RelativePath, IReadOnlyDictionary<string, Func<System.Text.Json.JsonElement, bool>> Rules)[] settings =
        [
            ("settings.json", GeneralSettings),
            ("FancyZones/settings.json", FancyZonesSettings),
            ("PowerRename/settings.json", PowerRenameSettings),
            ("Awake/settings.json", AwakeSettings),
        ];
        foreach ((string relative, IReadOnlyDictionary<string, Func<System.Text.Json.JsonElement, bool>> rules) in settings)
        {
            bool exists = context.Host.FileExists(
                Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            PluginCapturedFile? captured = await CaptureProjectedJsonFileAsync(
                context.Host,
                Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)),
                relative,
                rules,
                cancellationToken).ConfigureAwait(false);
            if (captured is not null)
            {
                files.Add(captured);
            }
            else if (exists)
            {
                warnings.Add($"InvalidSettingsFile:{relative}");
            }
        }

        return Snapshot(values, files, exclusions, warnings);
    }

    public override Task<IReadOnlyList<PluginSensitiveExclusion>> GetSensitiveExclusionsAsync(
        PluginCaptureContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<PluginSensitiveExclusion>>(
        [
            Exclusion("PowerToys logs", "Logs", "Diagnostic logs are not captured."),
            Exclusion("PowerToys cache", "Cache", "Runtime caches are not captured."),
            Exclusion("FancyZones layouts/history and Keyboard Manager mappings", "OpenEndedConfiguration", "Open-ended application and command mappings require manual recreation."),
            Exclusion("unrecognized PowerToys settings", "UnknownSchema", "Only explicitly supported preference keys and value types are captured."),
        ]);
    }
}

public sealed class EverythingPlugin : BuiltInApplicationPluginBase
{
    public override string Id => "replica.application.everything";

    public override string DisplayName => "Everything";

    protected override BuiltInApplication Application => BuiltInApplication.Everything;

    protected override string? PackageIdentifier => "voidtools.Everything";

    protected override bool IsSupportedVersion(string version) => VersionMajorIs(version, 1);

    public override async Task<PluginSnapshot> CaptureAsync(
        PluginCaptureContext context,
        CancellationToken cancellationToken)
    {
        BuiltInApplicationInfo info = context.Host.GetApplicationInfo(Application);
        Dictionary<string, string> values = ApplicationValues(info);
        List<string> warnings = ApplicationWarnings(info);
        IReadOnlyList<PluginSensitiveExclusion> exclusions = await GetSensitiveExclusionsAsync(
            context,
            cancellationToken).ConfigureAwait(false);
        if (!info.IsInstalled)
        {
            return Snapshot(values, [], exclusions, warnings);
        }

        string appData = context.Host.GetKnownPath(DeveloperKnownPath.RoamingApplicationData);
        string localAppData = context.Host.GetKnownPath(DeveloperKnownPath.LocalApplicationData);
        string[] candidates =
        [
            Path.Combine(appData, "Everything", "Everything.ini"),
            Path.Combine(localAppData, "Everything", "Everything.ini"),
            info.ExecutablePath is null
                ? string.Empty
                : Path.Combine(Path.GetDirectoryName(info.ExecutablePath)!, "Everything.ini"),
        ];
        string? path = candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .FirstOrDefault(context.Host.FileExists);
        if (path is not null)
        {
            string? content = await context.Host.ReadTextFileAsync(path, cancellationToken)
                .ConfigureAwait(false);
            if (content is not null)
            {
                foreach ((string key, string value) in ParseAllowListedConfiguration(
                             content,
                             IsAllowedEverythingKey))
                {
                    values[$"everything.{key}"] = value;
                }
            }
            else
            {
                warnings.Add("InvalidSettingsFile:Everything.ini");
            }
        }

        return Snapshot(values, [], exclusions, warnings);
    }

    public override Task<IReadOnlyList<PluginSensitiveExclusion>> GetSensitiveExclusionsAsync(
        PluginCaptureContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<PluginSensitiveExclusion>>(
        [
            Exclusion("Everything.db", "GeneratedDatabase", "The Everything index database is rebuilt and never captured."),
            Exclusion("Everything IPC state", "RuntimeState", "Runtime and instance state are not captured."),
        ]);
    }

    private static bool IsAllowedEverythingKey(string key) =>
        key.Equals("search_match_case", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("search_match_path", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("search_match_whole_word", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("search_match_diacritics", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("view", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("sort", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("window_x", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("window_y", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("window_wide", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("window_high", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("window_maximized", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("index_size", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("include_list", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("exclude_list", StringComparison.OrdinalIgnoreCase);
}

public sealed class ObsStudioPlugin : BuiltInApplicationPluginBase
{
    public override string Id => "replica.application.obs-studio";

    public override string DisplayName => "OBS Studio";

    protected override BuiltInApplication Application => BuiltInApplication.ObsStudio;

    protected override string? PackageIdentifier => "OBSProject.OBSStudio";

    protected override bool IsSupportedVersion(string version) =>
        VersionMajorIs(version, 28, 29, 30, 31, 32);

    public override async Task<PluginSnapshot> CaptureAsync(
        PluginCaptureContext context,
        CancellationToken cancellationToken)
    {
        BuiltInApplicationInfo info = context.Host.GetApplicationInfo(Application);
        Dictionary<string, string> values = ApplicationValues(info);
        List<string> warnings = ApplicationWarnings(info);
        IReadOnlyList<PluginSensitiveExclusion> exclusions = await GetSensitiveExclusionsAsync(
            context,
            cancellationToken).ConfigureAwait(false);
        if (!info.IsInstalled)
        {
            return Snapshot(values, [], exclusions, warnings);
        }

        string root = Path.Combine(
            context.Host.GetKnownPath(DeveloperKnownPath.RoamingApplicationData),
            "obs-studio");
        string profiles = Path.Combine(root, "basic", "profiles");
        int profileIndex = 0;
        foreach (string path in context.Host.EnumerateFiles(profiles, "basic.ini", recursive: true).Take(20))
        {
            string? content = await context.Host.ReadTextFileAsync(path, cancellationToken)
                .ConfigureAwait(false);
            if (content is null)
            {
                continue;
            }

            foreach ((string key, string value) in ParseAllowListedConfiguration(
                         content,
                         IsAllowedObsProfileKey))
            {
                values[$"obs.profile.{profileIndex}.{key.ToLowerInvariant()}"] = value;
            }

            profileIndex++;
        }

        return Snapshot(values, [], exclusions, warnings);
    }

    public override async Task<IReadOnlyList<PluginRestoreAction>> BuildRestoreActionsAsync(
        PluginComparisonResult comparison,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<PluginRestoreAction> actions = await base.BuildRestoreActionsAsync(
            comparison,
            cancellationToken).ConfigureAwait(false);
        if (comparison.Warnings?.Contains("ApplicationRunning", StringComparer.Ordinal) != true)
        {
            return actions;
        }

        return actions.Select(action => action with
        {
            Type = RestoreActionType.ManualInstruction,
            Description = "Close OBS Studio, rescan, and create a new reviewed restore plan.",
            IsManualOnly = true,
            SupportedStrategies = [PluginRestoreStrategy.KeepCurrent, PluginRestoreStrategy.Validate],
        }).ToArray();
    }

    public override Task<IReadOnlyList<PluginSensitiveExclusion>> GetSensitiveExclusionsAsync(
        PluginCaptureContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<PluginSensitiveExclusion>>(
        [
            Exclusion("service.json", "StreamingCredential", "Stream keys, service tokens, and authentication settings are never captured."),
            Exclusion("plugin_config/obs-browser", "BrowserCredential", "Browser source cookies and login state are never captured."),
            Exclusion("scene collections, sources, and hotkeys", "OpenEndedConfiguration", "Scene JSON can contain URLs, scripts, credentials, and private source data, so it requires manual recreation."),
            Exclusion("unrecognized profile settings", "UnknownSchema", "Only explicitly supported audio and video scalar keys are captured."),
            Exclusion("logs and crashes", "Logs", "OBS logs and crash reports are not captured."),
        ]);
    }

    private static bool IsAllowedObsProfileKey(string key) =>
        key.Equals("SampleRate", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("ChannelSetup", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("BaseCX", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("BaseCY", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("OutputCX", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("OutputCY", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("FPSType", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("FPSCommon", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("ColorFormat", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("ColorSpace", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("ColorRange", StringComparison.OrdinalIgnoreCase);
}

public sealed class MinecraftPlugin : BuiltInApplicationPluginBase
{
    public override string Id => "replica.application.minecraft";

    public override string DisplayName => "Minecraft Java Edition";

    protected override BuiltInApplication Application => BuiltInApplication.Minecraft;

    protected override string? PackageIdentifier => "Microsoft.MinecraftLauncher";

    protected override bool IsSupportedVersion(string version) => VersionMajorIs(version, 1, 2);

    public override async Task<PluginSnapshot> CaptureAsync(
        PluginCaptureContext context,
        CancellationToken cancellationToken)
    {
        BuiltInApplicationInfo info = context.Host.GetApplicationInfo(Application);
        Dictionary<string, string> values = ApplicationValues(info);
        List<PluginCapturedFile> files = [];
        List<string> warnings = ApplicationWarnings(info);
        IReadOnlyList<PluginSensitiveExclusion> exclusions = await GetSensitiveExclusionsAsync(
            context,
            cancellationToken).ConfigureAwait(false);
        if (!info.IsInstalled)
        {
            return Snapshot(values, files, exclusions, warnings);
        }

        string root = Path.Combine(
            context.Host.GetKnownPath(DeveloperKnownPath.RoamingApplicationData),
            ".minecraft");
        string? options = await context.Host.ReadTextFileAsync(
            Path.Combine(root, "options.txt"),
            cancellationToken).ConfigureAwait(false);
        if (options is not null)
        {
            foreach (string line in options.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                int separator = line.IndexOf(':');
                if (separator <= 0)
                {
                    continue;
                }

                string key = line[..separator].Trim();
                string value = line[(separator + 1)..].Trim();
                if (IsAllowedMinecraftOption(key, value))
                {
                    values[$"minecraft.option.{key.ToLowerInvariant()}"] = value;
                }
            }
        }
        AddFileManifest("resourcepack", Path.Combine(root, "resourcepacks"), "*");
        AddFileManifest("shaderpack", Path.Combine(root, "shaderpacks"), "*");
        AddFileManifest("mod", Path.Combine(root, "mods"), "*.jar");

        string configRoot = Path.Combine(root, "config");
        foreach (string pattern in new[] { "*.json", "*.toml", "*.cfg", "*.properties" })
        {
            foreach (string path in context.Host
                         .EnumerateFiles(configRoot, pattern, recursive: true)
                         .Take(256))
            {
                long? size = context.Host.GetFileSize(path);
                values[$"config-file:{RelativeLogicalPath(configRoot, path)}"] = size is null
                    ? "manifest-only"
                    : $"manifest-only;size={size.Value}";
            }
        }

        string savesRoot = Path.Combine(root, "saves");
        foreach (string selected in context.SelectedRecoveryPaths ?? [])
        {
            if (IsPathUnder(selected, savesRoot))
            {
                values[$"selected-save:{RelativeLogicalPath(savesRoot, selected)}"] =
                    "RecoverySnapshotSelection";
            }
        }

        return Snapshot(values, files, exclusions, warnings);

        void AddFileManifest(string kind, string path, string pattern)
        {
            foreach (string item in context.Host.EnumerateFiles(path, pattern, recursive: false))
            {
                long? size = context.Host.GetFileSize(item);
                values[$"{kind}:{Path.GetFileName(item)}"] = size is null
                    ? "manifest-only"
                    : $"manifest-only;size={size.Value}";
            }
        }
    }

    public override Task<IReadOnlyList<PluginSensitiveExclusion>> GetSensitiveExclusionsAsync(
        PluginCaptureContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<PluginSensitiveExclusion>>(
        [
            Exclusion("screenshots", "UserContent", "Screenshots require a separate explicit Recovery selection."),
            Exclusion("logs and crash-reports", "Logs", "Game logs and crash reports are not captured."),
            Exclusion("launcher authentication", "Credential", "Launcher account and authentication data are never captured."),
            Exclusion("unrecognized options.txt keys", "UnknownSchema", "Only explicitly supported numeric display and input preferences are captured."),
            Exclusion("mods/*.jar payload", "BinaryManifestOnly", "Only mod file names are recorded; JAR payloads are excluded."),
            Exclusion("game binaries", "ApplicationBinary", "Minecraft and runtime binaries are reacquired, not captured."),
            Exclusion("saves", "ExplicitRecoverySelection", "World payloads are handled only by explicit Recovery Snapshot selections."),
        ]);
    }

    private static bool IsAllowedMinecraftOption(string key, string value)
    {
        if (key.Equals("fov", StringComparison.OrdinalIgnoreCase) &&
            double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double fov))
        {
            return fov is >= -1 and <= 1;
        }

        return (key.Equals("guiScale", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("renderDistance", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("simulationDistance", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("maxFps", StringComparison.OrdinalIgnoreCase)) &&
            int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int number) &&
            number is >= 0 and <= 1000;
    }
}

public sealed class DockerDesktopPlugin : BuiltInApplicationPluginBase
{
    private static readonly IReadOnlyDictionary<string, Func<System.Text.Json.JsonElement, bool>> SupportedSettings =
        new Dictionary<string, Func<System.Text.Json.JsonElement, bool>>(StringComparer.Ordinal)
        {
            ["memoryMiB"] = value => IsJsonIntegerInRange(value, 512, 1_048_576),
            ["cpus"] = value => IsJsonIntegerInRange(value, 1, 1024),
            ["swapMiB"] = value => IsJsonIntegerInRange(value, 0, 1_048_576),
            ["diskSizeMiB"] = value => IsJsonIntegerInRange(value, 1024, int.MaxValue),
            ["useWslEngine"] = IsJsonBoolean,
            ["useGrpcfuse"] = IsJsonBoolean,
        };

    public override string Id => "replica.application.docker-desktop";

    public override string DisplayName => "Docker Desktop";

    protected override BuiltInApplication Application => BuiltInApplication.DockerDesktop;

    protected override string? PackageIdentifier => "Docker.DockerDesktop";

    protected override bool IsSupportedVersion(string version) => VersionAtLeast(version, 4);

    public override async Task<PluginSnapshot> CaptureAsync(
        PluginCaptureContext context,
        CancellationToken cancellationToken)
    {
        BuiltInApplicationInfo info = context.Host.GetApplicationInfo(Application);
        Dictionary<string, string> values = ApplicationValues(info);
        List<PluginCapturedFile> files = [];
        List<string> warnings = ApplicationWarnings(info);
        IReadOnlyList<PluginSensitiveExclusion> exclusions = await GetSensitiveExclusionsAsync(
            context,
            cancellationToken).ConfigureAwait(false);
        if (!info.IsInstalled)
        {
            return Snapshot(values, files, exclusions, warnings);
        }

        string root = Path.Combine(
            context.Host.GetKnownPath(DeveloperKnownPath.RoamingApplicationData),
            "Docker");
        foreach (string name in new[] { "settings-store.json", "settings.json" })
        {
            bool exists = context.Host.FileExists(Path.Combine(root, name));
            PluginCapturedFile? captured = await CaptureProjectedJsonFileAsync(
                context.Host,
                Path.Combine(root, name),
                name,
                SupportedSettings,
                cancellationToken).ConfigureAwait(false);
            if (captured is not null)
            {
                files.Add(captured);
            }
            else if (exists)
            {
                warnings.Add($"InvalidSettingsFile:{name}");
            }
        }

        return Snapshot(values, files, exclusions, warnings);
    }

    public override Task<IReadOnlyList<PluginSensitiveExclusion>> GetSensitiveExclusionsAsync(
        PluginCaptureContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<PluginSensitiveExclusion>>(
        [
            Exclusion("Docker images, containers, and volumes", "RuntimeData", "Docker runtime data is never captured."),
            Exclusion("registry credentials", "Credential", "Registry credentials and login state are never captured."),
            Exclusion("Kubernetes secrets", "KubernetesSecret", "Kubernetes secret data is never captured."),
            Exclusion("per-distribution WSL mappings and unrecognized Docker settings", "UnknownSchema", "Only explicit numeric resource limits and boolean engine preferences are captured."),
            Exclusion("Docker/wsl/*.vhdx", "WslDisk", "Complete WSL virtual disks are never captured."),
        ]);
    }
}

public sealed class AbletonLivePlugin : BuiltInApplicationPluginBase
{
    public override string Id => "replica.application.ableton-live";

    public override string DisplayName => "Ableton Live";

    protected override BuiltInApplication Application => BuiltInApplication.AbletonLive;

    protected override string? PackageIdentifier => null;

    protected override bool IsSupportedVersion(string version) => VersionMajorIs(version, 11, 12);

    public override async Task<PluginSnapshot> CaptureAsync(
        PluginCaptureContext context,
        CancellationToken cancellationToken)
    {
        BuiltInApplicationInfo info = context.Host.GetApplicationInfo(Application);
        Dictionary<string, string> values = ApplicationValues(info);
        List<string> warnings = ApplicationWarnings(info);
        IReadOnlyList<PluginSensitiveExclusion> exclusions = await GetSensitiveExclusionsAsync(
            context,
            cancellationToken).ConfigureAwait(false);
        if (!info.IsInstalled)
        {
            return Snapshot(values, [], exclusions, warnings);
        }

        string root = Path.Combine(
            context.Host.GetKnownPath(DeveloperKnownPath.RoamingApplicationData),
            "Ableton");
        foreach (string pattern in new[] { "Preferences.cfg", "Library.cfg", "Options.txt" })
        {
            foreach (string path in context.Host.EnumerateFiles(root, pattern, recursive: true).Take(20))
            {
                string? content = await context.Host.ReadTextFileAsync(path, cancellationToken)
                    .ConfigureAwait(false);
                if (content is null)
                {
                    continue;
                }

                if (Path.GetFileName(path).Equals("Options.txt", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (string option in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (option.Trim().Equals("-EnableArmOnSelection", StringComparison.Ordinal))
                        {
                            values["ableton.option.enable-arm-on-selection"] = "true";
                        }
                    }

                    continue;
                }

                foreach ((string key, string value) in ParseAllowListedConfiguration(
                             content,
                             IsAllowedAbletonKey))
                {
                    values[$"ableton.preference.{key.ToLowerInvariant()}"] = value;
                }
            }
        }

        return Snapshot(values, [], exclusions, warnings);
    }

    public override Task<IReadOnlyList<PluginSensitiveExclusion>> GetSensitiveExclusionsAsync(
        PluginCaptureContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<PluginSensitiveExclusion>>(
        [
            Exclusion("Packs", "LicensedContent", "Paid Ableton Packs are not copied."),
            Exclusion("VST binaries", "PluginBinary", "Only explicitly allow-listed VST search-path values are inventoried, never plugin binaries."),
            Exclusion("licenses and authorization", "License", "License and authorization data are never captured."),
            Exclusion("unrecognized Ableton preference keys", "UnknownSchema", "Opaque preference-file bodies are not copied into the snapshot."),
            Exclusion("sample libraries", "LargeLibrary", "Large sample libraries are not captured."),
            Exclusion("projects", "ExplicitRecoverySelection", "Projects require a separate explicit Recovery Snapshot selection."),
        ]);
    }

    private static bool IsAllowedAbletonKey(string key) =>
        key.Equals("UserLibraryPath", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("TemplatePath", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("VST3Path", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("LibraryPath", StringComparison.OrdinalIgnoreCase);
}
