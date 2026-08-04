using Replica.Core.Planning;
using Replica.Core.Plugins;
using static Replica.Plugins.BuiltIn.DeveloperPluginHelpers;

namespace Replica.Plugins.BuiltIn;

public sealed class PowerToysPlugin : BuiltInApplicationPluginBase
{
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
        string[] settings =
        [
            "settings.json",
            "FancyZones/settings.json",
            "FancyZones/custom-layouts.json",
            "FancyZones/app-zone-history.json",
            "Keyboard Manager/default.json",
            "PowerRename/settings.json",
            "AlwaysOnTop/settings.json",
            "Awake/settings.json",
        ];
        foreach (string relative in settings)
        {
            await AddJsonFileAsync(
                context,
                Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)),
                relative,
                files,
                warnings,
                redactUrlQueries: false,
                cancellationToken).ConfigureAwait(false);
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

    private static bool IsAllowedEverythingKey(string key)
    {
        string[] prefixes =
        [
            "search_",
            "match_",
            "sort",
            "view",
            "window_",
            "index_",
            "include_",
            "exclude_",
            "folder_",
            "show_",
        ];
        return prefixes.Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
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
            "obs-studio");
        await AddTextFileAsync(
            context,
            Path.Combine(root, "global.ini"),
            "global.ini",
            files,
            cancellationToken).ConfigureAwait(false);

        string scenes = Path.Combine(root, "basic", "scenes");
        foreach (string path in context.Host.EnumerateFiles(scenes, "*.json", recursive: false).Take(100))
        {
            await AddJsonFileAsync(
                context,
                path,
                $"scenes/{Path.GetFileName(path)}",
                files,
                warnings,
                redactUrlQueries: true,
                cancellationToken).ConfigureAwait(false);
        }

        string profiles = Path.Combine(root, "basic", "profiles");
        foreach (string path in context.Host.EnumerateFiles(profiles, "basic.ini", recursive: true).Take(100))
        {
            await AddTextFileAsync(
                context,
                path,
                $"profiles/{RelativeLogicalPath(profiles, path)}",
                files,
                cancellationToken).ConfigureAwait(false);
        }

        return Snapshot(values, files, exclusions, warnings);
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
            Exclusion("URL query values", "SensitiveUrlQuery", "Query components are removed from captured scene URLs."),
            Exclusion("logs and crashes", "Logs", "OBS logs and crash reports are not captured."),
        ]);
    }
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
        const int maximumConfigFiles = 256;
        const int maximumConfigCharacters = 2 * 1024 * 1024;
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
        await AddTextFileAsync(
            context,
            Path.Combine(root, "options.txt"),
            "options.txt",
            files,
            cancellationToken).ConfigureAwait(false);
        AddFileManifest("resourcepack", Path.Combine(root, "resourcepacks"), "*");
        AddFileManifest("shaderpack", Path.Combine(root, "shaderpacks"), "*");
        AddFileManifest("mod", Path.Combine(root, "mods"), "*.jar");

        string configRoot = Path.Combine(root, "config");
        int capturedCharacters = files.Sum(file => file.Content.Length);
        foreach (string pattern in new[] { "*.json", "*.toml", "*.cfg", "*.properties" })
        {
            foreach (string path in context.Host.EnumerateFiles(configRoot, pattern, recursive: true))
            {
                if (files.Count >= maximumConfigFiles || capturedCharacters >= maximumConfigCharacters)
                {
                    warnings.Add("ConfigurationCaptureLimitReached");
                    break;
                }

                PluginCapturedFile? captured = await CaptureFileAsync(
                    context.Host,
                    path,
                    $"config/{RelativeLogicalPath(configRoot, path)}",
                    sanitizeJson: path.EndsWith(".json", StringComparison.OrdinalIgnoreCase),
                    cancellationToken).ConfigureAwait(false);
                if (captured is null)
                {
                    if (context.Host.FileExists(path))
                    {
                        warnings.Add($"InvalidSettingsFile:{Path.GetFileName(path)}");
                    }

                    continue;
                }

                if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    captured = captured with { Content = SanitizeTextContent(captured.Content) };
                }

                if (capturedCharacters + captured.Content.Length > maximumConfigCharacters)
                {
                    warnings.Add("ConfigurationCaptureLimitReached");
                    break;
                }

                files.Add(captured);
                capturedCharacters += captured.Content.Length;
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
            Exclusion("mods/*.jar payload", "BinaryManifestOnly", "Only mod file names are recorded; JAR payloads are excluded."),
            Exclusion("game binaries", "ApplicationBinary", "Minecraft and runtime binaries are reacquired, not captured."),
            Exclusion("saves", "ExplicitRecoverySelection", "World payloads are handled only by explicit Recovery Snapshot selections."),
        ]);
    }
}

public sealed class DockerDesktopPlugin : BuiltInApplicationPluginBase
{
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
            await AddJsonFileAsync(
                context,
                Path.Combine(root, name),
                name,
                files,
                warnings,
                redactUrlQueries: false,
                cancellationToken).ConfigureAwait(false);
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
            "Ableton");
        foreach (string pattern in new[] { "Preferences.cfg", "Library.cfg", "Options.txt" })
        {
            foreach (string path in context.Host.EnumerateFiles(root, pattern, recursive: true).Take(20))
            {
                await AddTextFileAsync(
                    context,
                    path,
                    RelativeLogicalPath(root, path),
                    files,
                    cancellationToken).ConfigureAwait(false);
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
            Exclusion("Packs", "LicensedContent", "Paid Ableton Packs are not copied."),
            Exclusion("VST binaries", "PluginBinary", "Only configured VST search paths are captured, never plugin binaries."),
            Exclusion("licenses and authorization", "License", "License and authorization data are never captured."),
            Exclusion("sample libraries", "LargeLibrary", "Large sample libraries are not captured."),
            Exclusion("projects", "ExplicitRecoverySelection", "Projects require a separate explicit Recovery Snapshot selection."),
        ]);
    }
}
