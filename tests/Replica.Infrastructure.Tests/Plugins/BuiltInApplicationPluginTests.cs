using System.Text.Json;
using Replica.Core.Diffing;
using Replica.Core.Planning;
using Replica.Core.Plugins;
using Replica.Plugins.BuiltIn;

namespace Replica.Infrastructure.Tests.Plugins;

public sealed class BuiltInApplicationPluginTests
{
    public static TheoryData<string> ApplicationFixtures => new()
    {
        "powertoys",
        "everything",
        "obs",
        "minecraft",
        "docker",
        "ableton",
    };

    public static TheoryData<string, string> UnsupportedVersions => new()
    {
        { "powertoys", "0.50.0" },
        { "everything", "2.0.0" },
        { "obs", "27.2.4" },
        { "minecraft", "3.0" },
        { "docker", "3.9.0" },
        { "ableton", "10.1.0" },
    };

    [Theory]
    [MemberData(nameof(ApplicationFixtures))]
    public async Task CaptureAsync_UsesFixtureAndRemovesSensitiveData(string fixtureName)
    {
        FixtureApplicationPluginHost host = FixtureApplicationPluginHost.Load(fixtureName);
        IBuiltInPlugin plugin = CreatePlugin(fixtureName);

        PluginSnapshot snapshot = await plugin.CaptureAsync(
            new PluginCaptureContext(host),
            CancellationToken.None);
        string serialized = JsonSerializer.Serialize(snapshot);

        Assert.Equal("True", snapshot.Values["application.installed"]);
        Assert.NotEmpty(snapshot.Exclusions);
        Assert.DoesNotContain("TOP_SECRET", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("OPAQUE_CREDENTIAL_48391", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("serviceToken", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("streamKey", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(ApplicationFixtures))]
    public async Task DetectAsync_ReportsInstalledSupportedFixture(string fixtureName)
    {
        FixtureApplicationPluginHost host = FixtureApplicationPluginHost.Load(fixtureName);
        IBuiltInPlugin plugin = CreatePlugin(fixtureName);

        PluginDetectionResult detection = await plugin.DetectAsync(
            new PluginCaptureContext(host),
            CancellationToken.None);

        Assert.True(detection.IsDetected);
        Assert.NotNull(detection.DetectedVersion);
        Assert.DoesNotContain("UnsupportedVersion", detection.Warnings);
    }

    [Theory]
    [MemberData(nameof(UnsupportedVersions))]
    public async Task DetectAsync_ReportsUnsupportedVersion(string fixtureName, string version)
    {
        FixtureApplicationPluginHost host = FixtureApplicationPluginHost.Load(fixtureName);
        host.Version = version;
        IBuiltInPlugin plugin = CreatePlugin(fixtureName);

        PluginDetectionResult detection = await plugin.DetectAsync(
            new PluginCaptureContext(host),
            CancellationToken.None);

        Assert.True(detection.IsDetected);
        Assert.Contains("UnsupportedVersion", detection.Warnings);
    }

    [Theory]
    [MemberData(nameof(ApplicationFixtures))]
    public async Task CaptureAsync_WhenNotInstalled_DoesNotReadSettings(string fixtureName)
    {
        FixtureApplicationPluginHost host = FixtureApplicationPluginHost.Load(fixtureName);
        host.IsInstalled = false;
        IBuiltInPlugin plugin = CreatePlugin(fixtureName);

        PluginSnapshot snapshot = await plugin.CaptureAsync(
            new PluginCaptureContext(host),
            CancellationToken.None);

        Assert.Equal("False", snapshot.Values["application.installed"]);
        Assert.Empty(snapshot.Files);
        Assert.DoesNotContain(snapshot.Values.Keys, key =>
            key.StartsWith("selected-save:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PowerToys_CapturesOnlySchemaAllowListedPreferencesAndReportsInvalidJson()
    {
        FixtureApplicationPluginHost host = FixtureApplicationPluginHost.Load("powertoys");
        PowerToysPlugin plugin = new();
        PluginSnapshot snapshot = await plugin.CaptureAsync(
            new PluginCaptureContext(host),
            CancellationToken.None);

        Assert.Contains(snapshot.Files, file => file.LogicalPath == "settings.json");
        Assert.Contains(snapshot.Files, file => file.LogicalPath == "FancyZones/settings.json");
        Assert.Contains(snapshot.Files, file => file.LogicalPath == "PowerRename/settings.json");
        Assert.Contains(snapshot.Files, file => file.LogicalPath == "Awake/settings.json");
        Assert.DoesNotContain(snapshot.Files, file => file.LogicalPath.Contains("Keyboard Manager", StringComparison.Ordinal));
        Assert.DoesNotContain(snapshot.Files, file => file.LogicalPath.Contains("app-zone-history", StringComparison.Ordinal));

        host.SetFile("{LocalAppData}/Microsoft/PowerToys/settings.json", "{invalid-json");
        PluginSnapshot invalid = await plugin.CaptureAsync(
            new PluginCaptureContext(host),
            CancellationToken.None);

        Assert.Contains("InvalidSettingsFile:settings.json", invalid.Warnings);
    }

    [Fact]
    public async Task Everything_CapturesAllowListedSettingsButNotDatabase()
    {
        FixtureApplicationPluginHost host = FixtureApplicationPluginHost.Load("everything");
        EverythingPlugin plugin = new();

        PluginSnapshot snapshot = await plugin.CaptureAsync(
            new PluginCaptureContext(host),
            CancellationToken.None);

        Assert.Equal("details", snapshot.Values["everything.view"]);
        Assert.Contains(snapshot.Values.Keys, key => key == "everything.exclude_list");
        Assert.DoesNotContain(snapshot.Values.Keys, key =>
            key.Contains("token", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(snapshot.Files);
    }

    [Fact]
    public async Task Obs_CapturesOnlyAllowListedProfileScalars()
    {
        FixtureApplicationPluginHost host = FixtureApplicationPluginHost.Load("obs");
        ObsStudioPlugin plugin = new();

        PluginSnapshot snapshot = await plugin.CaptureAsync(
            new PluginCaptureContext(host),
            CancellationToken.None);
        string serialized = JsonSerializer.Serialize(snapshot);

        Assert.Empty(snapshot.Files);
        Assert.Contains(snapshot.Values, pair => pair.Key.EndsWith("samplerate", StringComparison.Ordinal) && pair.Value == "48000");
        Assert.Contains(snapshot.Values, pair => pair.Key.EndsWith("basecx", StringComparison.Ordinal) && pair.Value == "1920");
        Assert.DoesNotContain("OBS_KEY_F9", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("?token=", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Obs_WhenRunning_ProducesManualOnlyRestoreActions()
    {
        FixtureApplicationPluginHost host = FixtureApplicationPluginHost.Load("obs");
        ObsStudioPlugin plugin = new();
        PluginSnapshot source = await plugin.CaptureAsync(
            new PluginCaptureContext(host),
            CancellationToken.None);
        host.IsRunning = true;
        host.SetFile(
            "{AppData}/obs-studio/basic/profiles/Streaming/basic.ini",
            "[Audio]\nSampleRate=44100\n[Video]\nBaseCX=1280\nBaseCY=720\n");
        PluginSnapshot target = await plugin.CaptureAsync(
            new PluginCaptureContext(host),
            CancellationToken.None);
        PluginComparisonResult comparison = await plugin.CompareAsync(
            source,
            target,
            CancellationToken.None);

        IReadOnlyList<PluginRestoreAction> actions = await plugin.BuildRestoreActionsAsync(
            comparison,
            CancellationToken.None);

        Assert.NotEmpty(actions);
        Assert.All(actions, action =>
        {
            Assert.True(action.IsManualOnly);
            Assert.Equal(RestoreActionType.ManualInstruction, action.Type);
        });
    }

    [Fact]
    public async Task Minecraft_RecordsManifestsAndOnlyExplicitSaveSelection()
    {
        FixtureApplicationPluginHost host = FixtureApplicationPluginHost.Load("minecraft");
        MinecraftPlugin plugin = new();
        PluginSnapshot defaultCapture = await plugin.CaptureAsync(
            new PluginCaptureContext(host),
            CancellationToken.None);
        string world = Path.Combine(
            host.GetKnownPath(DeveloperKnownPath.RoamingApplicationData),
            ".minecraft",
            "saves",
            "Replica World");
        PluginSnapshot selectedCapture = await plugin.CaptureAsync(
            new PluginCaptureContext(host, SelectedRecoveryPaths: [world]),
            CancellationToken.None);

        Assert.Contains(defaultCapture.Values.Keys, key => key.StartsWith(
            "resourcepack:",
            StringComparison.Ordinal));
        Assert.Contains(defaultCapture.Values.Keys, key => key.StartsWith(
            "shaderpack:",
            StringComparison.Ordinal));
        Assert.Contains(defaultCapture.Values, pair =>
            pair.Key.StartsWith("mod:", StringComparison.Ordinal) &&
            pair.Value.Contains("size=", StringComparison.Ordinal));
        Assert.DoesNotContain(defaultCapture.Values.Keys, key => key.StartsWith(
            "selected-save:",
            StringComparison.Ordinal));
        Assert.Contains(selectedCapture.Values.Keys, key => key.StartsWith(
            "selected-save:",
            StringComparison.Ordinal));
        Assert.DoesNotContain(defaultCapture.Files, file =>
            file.LogicalPath.Contains("saves", StringComparison.OrdinalIgnoreCase) ||
            file.LogicalPath.EndsWith(".jar", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Docker_CapturesOnlyAllowListedResourcesWithoutRuntimeData()
    {
        FixtureApplicationPluginHost host = FixtureApplicationPluginHost.Load("docker");
        DockerDesktopPlugin plugin = new();

        PluginSnapshot snapshot = await plugin.CaptureAsync(
            new PluginCaptureContext(host),
            CancellationToken.None);
        string serialized = JsonSerializer.Serialize(snapshot);

        Assert.Contains("memoryMiB", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("wslIntegrations", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(snapshot.Files, file => file.LogicalPath.EndsWith(
            ".vhdx",
            StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("registryCredential", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ableton_ProjectsAllowListedPreferencesWithoutOpaqueFiles()
    {
        FixtureApplicationPluginHost host = FixtureApplicationPluginHost.Load("ableton");
        AbletonLivePlugin plugin = new();

        PluginSnapshot snapshot = await plugin.CaptureAsync(
            new PluginCaptureContext(host),
            CancellationToken.None);
        string content = JsonSerializer.Serialize(snapshot.Values);

        Assert.Empty(snapshot.Files);
        Assert.Contains("userlibrarypath", content, StringComparison.Ordinal);
        Assert.Contains("templatepath", content, StringComparison.Ordinal);
        Assert.Contains("vst3path", content, StringComparison.Ordinal);
        Assert.DoesNotContain(snapshot.Files, file =>
            file.LogicalPath.EndsWith(".als", StringComparison.OrdinalIgnoreCase) ||
            file.LogicalPath.Contains("Packs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CompareAndValidate_ReportSettingConflictAndPostRestoreDrift()
    {
        FixtureApplicationPluginHost host = FixtureApplicationPluginHost.Load("powertoys");
        PowerToysPlugin plugin = new();
        PluginCaptureContext context = new(host);
        PluginSnapshot expected = await plugin.CaptureAsync(context, CancellationToken.None);
        PluginValidationResult valid = await plugin.ValidateAsync(
            expected,
            context,
            CancellationToken.None);
        host.SetFile(
            "{LocalAppData}/Microsoft/PowerToys/settings.json",
            "{\"startup\":false,\"theme\":\"light\"}");
        PluginSnapshot current = await plugin.CaptureAsync(context, CancellationToken.None);

        PluginComparisonResult comparison = await plugin.CompareAsync(
            expected,
            current,
            CancellationToken.None);
        PluginValidationResult invalid = await plugin.ValidateAsync(
            expected,
            context,
            CancellationToken.None);

        Assert.True(valid.IsValid);
        Assert.Contains(comparison.Differences, difference =>
            difference.Type == DiffType.FileChanged && difference.Key == "settings.json");
        Assert.False(invalid.IsValid);
    }

    [Fact]
    public async Task MissingApplication_BuildsAllowListedInstallOrLicensedManualAction()
    {
        FixtureApplicationPluginHost powerToysHost = FixtureApplicationPluginHost.Load("powertoys");
        PowerToysPlugin powerToys = new();
        PluginSnapshot powerToysSource = await powerToys.CaptureAsync(
            new PluginCaptureContext(powerToysHost),
            CancellationToken.None);
        powerToysHost.IsInstalled = false;
        PluginSnapshot powerToysTarget = await powerToys.CaptureAsync(
            new PluginCaptureContext(powerToysHost),
            CancellationToken.None);
        PluginComparisonResult powerToysComparison = await powerToys.CompareAsync(
            powerToysSource,
            powerToysTarget,
            CancellationToken.None);
        IReadOnlyList<PluginRestoreAction> powerToysActions =
            await powerToys.BuildRestoreActionsAsync(powerToysComparison, CancellationToken.None);

        FixtureApplicationPluginHost abletonHost = FixtureApplicationPluginHost.Load("ableton");
        AbletonLivePlugin ableton = new();
        PluginSnapshot abletonSource = await ableton.CaptureAsync(
            new PluginCaptureContext(abletonHost),
            CancellationToken.None);
        abletonHost.IsInstalled = false;
        PluginSnapshot abletonTarget = await ableton.CaptureAsync(
            new PluginCaptureContext(abletonHost),
            CancellationToken.None);
        PluginComparisonResult abletonComparison = await ableton.CompareAsync(
            abletonSource,
            abletonTarget,
            CancellationToken.None);
        IReadOnlyList<PluginRestoreAction> abletonActions = await ableton.BuildRestoreActionsAsync(
            abletonComparison,
            CancellationToken.None);

        Assert.Contains(powerToysActions, action =>
            action.Type == RestoreActionType.InstallPackage &&
            action.TargetValue == "Microsoft.PowerToys");
        Assert.Contains(abletonActions, action =>
            action.Type == RestoreActionType.ManualInstruction &&
            action.IsManualOnly);
    }

    private static IBuiltInPlugin CreatePlugin(string fixtureName)
    {
        return fixtureName switch
        {
            "powertoys" => new PowerToysPlugin(),
            "everything" => new EverythingPlugin(),
            "obs" => new ObsStudioPlugin(),
            "minecraft" => new MinecraftPlugin(),
            "docker" => new DockerDesktopPlugin(),
            "ableton" => new AbletonLivePlugin(),
            _ => throw new ArgumentOutOfRangeException(nameof(fixtureName), fixtureName, null),
        };
    }

    private sealed class FixtureApplicationPluginHost : IDeveloperPluginHost
    {
        private const string Root = "C:/ReplicaApplicationFixture";
        private readonly BuiltInApplication _application;
        private readonly Dictionary<string, string> _files;

        private FixtureApplicationPluginHost(
            BuiltInApplication application,
            bool installed,
            string? version,
            bool running,
            Dictionary<string, string> files)
        {
            _application = application;
            IsInstalled = installed;
            Version = version;
            IsRunning = running;
            _files = files;
        }

        public bool IsInstalled { get; set; }

        public string? Version { get; set; }

        public bool IsRunning { get; set; }

        public static FixtureApplicationPluginHost Load(string name)
        {
            string path = Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "ApplicationPlugins",
                $"{name}.json");
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            BuiltInApplication application = Enum.Parse<BuiltInApplication>(
                document.RootElement.GetProperty("application").GetString()!);
            Dictionary<string, string> files = new(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in document.RootElement.GetProperty("files").EnumerateObject())
            {
                files[Expand(property.Name)] = property.Value.GetString()!;
            }

            return new FixtureApplicationPluginHost(
                application,
                document.RootElement.GetProperty("installed").GetBoolean(),
                document.RootElement.GetProperty("version").GetString(),
                document.RootElement.GetProperty("running").GetBoolean(),
                files);
        }

        public string GetKnownPath(DeveloperKnownPath path)
        {
            return path switch
            {
                DeveloperKnownPath.UserProfile => $"{Root}/User",
                DeveloperKnownPath.RoamingApplicationData => $"{Root}/AppData/Roaming",
                DeveloperKnownPath.LocalApplicationData => $"{Root}/AppData/Local",
                DeveloperKnownPath.Documents => $"{Root}/User/Documents",
                _ => throw new ArgumentOutOfRangeException(nameof(path), path, null),
            };
        }

        public bool FileExists(string path) => _files.ContainsKey(Normalize(path));

        public bool DirectoryExists(string path)
        {
            string prefix = $"{Normalize(path)}/";
            return _files.Keys.Any(file => file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        public long? GetFileSize(string path) =>
            _files.TryGetValue(Normalize(path), out string? content) ? content.Length : null;

        public Task<string?> ReadTextFileAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _files.TryGetValue(Normalize(path), out string? content);
            return Task.FromResult(content);
        }

        public IReadOnlyList<string> EnumerateFiles(
            string path,
            string searchPattern,
            bool recursive)
        {
            string prefix = $"{Normalize(path)}/";
            return _files.Keys
                .Where(file => file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Where(file => recursive || !file[prefix.Length..].Contains('/'))
                .Where(file => PatternMatches(Path.GetFileName(file), searchPattern))
                .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public BuiltInApplicationInfo GetApplicationInfo(BuiltInApplication application)
        {
            return application == _application
                ? new BuiltInApplicationInfo(IsInstalled, Version, IsRunning)
                : new BuiltInApplicationInfo(false, null, false);
        }

        public Task<DeveloperToolQueryResult> QueryAsync(
            DeveloperToolQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new DeveloperToolQueryResult(false, string.Empty));
        }

        public void SetFile(string path, string content)
        {
            _files[Expand(path)] = content;
        }

        private static bool PatternMatches(string fileName, string pattern)
        {
            if (pattern == "*")
            {
                return true;
            }

            if (pattern.StartsWith("*.", StringComparison.Ordinal))
            {
                return fileName.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);
            }

            return fileName.Equals(pattern, StringComparison.OrdinalIgnoreCase);
        }

        private static string Expand(string value)
        {
            return Normalize(value
                .Replace("{UserProfile}", $"{Root}/User", StringComparison.Ordinal)
                .Replace("{AppData}", $"{Root}/AppData/Roaming", StringComparison.Ordinal)
                .Replace("{LocalAppData}", $"{Root}/AppData/Local", StringComparison.Ordinal)
                .Replace("{Documents}", $"{Root}/User/Documents", StringComparison.Ordinal));
        }

        private static string Normalize(string value) =>
            value.Replace('\\', '/').TrimEnd('/');
    }
}
