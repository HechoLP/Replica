using System.Text.Json;
using Replica.Core.Diffing;
using Replica.Core.Planning;
using Replica.Core.Plugins;
using Replica.Core.Snapshots;
using Replica.Infrastructure.Tests.Snapshots;
using Replica.Plugins.BuiltIn;

namespace Replica.Infrastructure.Tests.Plugins;

public sealed class BuiltInDeveloperPluginTests
{
    public static TheoryData<string> PluginFixtures => new()
    {
        "vscode",
        "git",
        "powershell",
        "terminal",
        "node",
        "python",
    };

    [Theory]
    [MemberData(nameof(PluginFixtures))]
    public async Task CaptureAsync_UsesFixtureAndNeverCapturesSensitiveSentinel(string fixtureName)
    {
        FixtureDeveloperPluginHost host = FixtureDeveloperPluginHost.Load(fixtureName);
        IBuiltInPlugin plugin = CreatePlugin(fixtureName);

        PluginSnapshot snapshot = await plugin.CaptureAsync(
            new PluginCaptureContext(host, IncludeSshPublicKeyFingerprints: true),
            CancellationToken.None);
        string serialized = JsonSerializer.Serialize(snapshot);

        Assert.Equal(plugin.Id, snapshot.PluginId);
        Assert.NotEmpty(snapshot.Exclusions);
        Assert.DoesNotContain("TOP_SECRET", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("_authToken", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VisualStudioCode_CapturesSupportedFilesAndVersionedExtensions()
    {
        FixtureDeveloperPluginHost host = FixtureDeveloperPluginHost.Load("vscode");
        VisualStudioCodePlugin plugin = new();

        PluginSnapshot snapshot = await plugin.CaptureAsync(
            new PluginCaptureContext(host),
            CancellationToken.None);

        Assert.Equal("2.80.0", snapshot.Values["extension:ms-dotnettools.csharp"]);
        Assert.Contains(snapshot.Files, file => file.LogicalPath == "settings.json");
        Assert.Contains(snapshot.Files, file => file.LogicalPath == "snippets/csharp.json");
        Assert.DoesNotContain(snapshot.Files, file =>
            file.LogicalPath.Contains("workspaceStorage", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Git_CapturesAllowListedConfigurationAndOptionalPublicFingerprintOnly()
    {
        FixtureDeveloperPluginHost host = FixtureDeveloperPluginHost.Load("git");
        GitPlugin plugin = new();

        PluginSnapshot snapshot = await plugin.CaptureAsync(
            new PluginCaptureContext(host, IncludeSshPublicKeyFingerprints: true),
            CancellationToken.None);

        Assert.Equal("manager", snapshot.Values["credential.helper"]);
        Assert.Contains(snapshot.Values.Keys, key => key.StartsWith(
            "ssh-public-key-fingerprint:",
            StringComparison.Ordinal));
        Assert.DoesNotContain(snapshot.Values.Keys, key =>
            key.Contains("password", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(snapshot.Files);
    }

    [Fact]
    public async Task PowerShell_CapturesProfileAndModulesWithoutPolicyOrRepositoryActions()
    {
        FixtureDeveloperPluginHost host = FixtureDeveloperPluginHost.Load("powershell");
        PowerShellPlugin plugin = new();

        PluginSnapshot snapshot = await plugin.CaptureAsync(
            new PluginCaptureContext(host),
            CancellationToken.None);
        PluginSnapshot empty = EmptySnapshot(plugin);
        PluginComparisonResult comparison = await plugin.CompareAsync(
            snapshot,
            empty,
            CancellationToken.None);
        IReadOnlyList<PluginRestoreAction> actions = await plugin.BuildRestoreActionsAsync(
            comparison,
            CancellationToken.None);

        Assert.Equal("5.7.1", snapshot.Values["powershell-module:Pester"]);
        Assert.Contains(snapshot.Files, file => file.Content.Contains(
            "Import-Module Pester",
            StringComparison.Ordinal));
        Assert.DoesNotContain(actions, action => action.Name.Contains(
            "ExecutionPolicy",
            StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(actions, action => action.Name.Contains(
            "repository",
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WindowsTerminal_UsesSemanticJsonComparisonAndWarnsForMissingShell()
    {
        FixtureDeveloperPluginHost host = FixtureDeveloperPluginHost.Load("terminal");
        WindowsTerminalPlugin plugin = new();
        PluginSnapshot captured = await plugin.CaptureAsync(
            new PluginCaptureContext(host),
            CancellationToken.None);
        PluginCapturedFile file = Assert.Single(captured.Files);
        PluginSnapshot reformatted = captured with
        {
            Files =
            [
                file with
                {
                    Content = JsonSerializer.Serialize(
                        JsonSerializer.Deserialize<JsonElement>(file.Content)),
                },
            ],
        };

        PluginComparisonResult comparison = await plugin.CompareAsync(
            captured,
            reformatted,
            CancellationToken.None);

        Assert.All(comparison.Differences, difference =>
            Assert.Equal(DiffType.ExactMatch, difference.Type));
        Assert.Contains(captured.Warnings, warning => warning.Contains(
            "not installed",
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Node_CapturesVersionsPackagesAndVersionManagerWithoutNpmrc()
    {
        FixtureDeveloperPluginHost host = FixtureDeveloperPluginHost.Load("node");
        NodeJsPlugin plugin = new();

        PluginSnapshot snapshot = await plugin.CaptureAsync(
            new PluginCaptureContext(host),
            CancellationToken.None);

        Assert.Equal("v24.4.0", snapshot.Values["node.version"]);
        Assert.Equal("5.8.3", snapshot.Values["npm-global-package:typescript"]);
        Assert.Equal("nvm", snapshot.Values["node.version-manager"]);
        Assert.Empty(snapshot.Files);
    }

    [Fact]
    public async Task Python_CapturesInterpreterPackagesAndVenvMetadataWithoutVenvContents()
    {
        FixtureDeveloperPluginHost host = FixtureDeveloperPluginHost.Load("python");
        PythonPlugin plugin = new();

        PluginSnapshot snapshot = await plugin.CaptureAsync(
            new PluginCaptureContext(host),
            CancellationToken.None);

        Assert.Contains(snapshot.Values, pair =>
            pair.Key.StartsWith("python-package:", StringComparison.Ordinal) &&
            pair.Key.EndsWith(":pytest", StringComparison.Ordinal) &&
            pair.Value == "8.4.0");
        Assert.Contains(snapshot.Values, pair =>
            pair.Key.StartsWith("python-venv:", StringComparison.Ordinal) &&
            pair.Value == "metadata-only");
        Assert.Empty(snapshot.Files);
    }

    [Fact]
    public async Task RestoreActions_AreTypedAndValidationDetectsDrift()
    {
        FixtureDeveloperPluginHost host = FixtureDeveloperPluginHost.Load("vscode");
        VisualStudioCodePlugin plugin = new();
        PluginSnapshot source = await plugin.CaptureAsync(
            new PluginCaptureContext(host),
            CancellationToken.None);
        PluginComparisonResult comparison = await plugin.CompareAsync(
            source,
            EmptySnapshot(plugin),
            CancellationToken.None);

        IReadOnlyList<PluginRestoreAction> actions = await plugin.BuildRestoreActionsAsync(
            comparison,
            CancellationToken.None);
        PluginValidationResult validation = await plugin.ValidateAsync(
            EmptySnapshot(plugin),
            new PluginCaptureContext(host),
            CancellationToken.None);

        Assert.Contains(actions, action => action.Type == RestoreActionType.InstallExtension);
        Assert.Contains(actions, action => action.Type == RestoreActionType.MergeJson);
        Assert.Contains(actions, action =>
            action.Type == RestoreActionType.InstallPackage &&
            action.TargetValue == "Microsoft.VisualStudioCode");
        PluginRestoreAction settings = Assert.Single(
            actions,
            action => action.Name.EndsWith("settings.json", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(PluginRestoreStrategy.Merge, settings.SupportedStrategies);
        Assert.Contains(PluginRestoreStrategy.Replace, settings.SupportedStrategies);
        Assert.Contains(PluginRestoreStrategy.KeepCurrent, settings.SupportedStrategies);
        Assert.DoesNotContain(actions, action => action.Id.Contains(' '));
        Assert.False(validation.IsValid);
    }

    [Fact]
    public async Task CapturedPluginData_RoundTripsThroughReplicaSnapshotWithoutSecrets()
    {
        using SnapshotTestContext snapshotContext = new();
        FixtureDeveloperPluginHost host = FixtureDeveloperPluginHost.Load("vscode");
        VisualStudioCodePlugin plugin = new();
        PluginSnapshot capture = await plugin.CaptureAsync(
            new PluginCaptureContext(host),
            CancellationToken.None);
        string snapshotPath = Path.Combine(snapshotContext.RootPath, "developer.replica");
        ReplicaSnapshotWriteRequest request = snapshotContext.CreateRequest(
            snapshotPath,
            SnapshotType.Lightweight);
        request = request with
        {
            Inventory = request.Inventory with
            {
                Plugins = [capture.ToReplicaSnapshot()],
            },
        };

        await snapshotContext.CreateWriter().WriteAsync(request, null, CancellationToken.None);
        ReplicaSnapshotReadResult result = await snapshotContext.CreateReader().ReadAsync(
            new ReplicaSnapshotReadRequest(snapshotPath),
            null,
            CancellationToken.None);
        ReplicaPluginSnapshot restored = Assert.Single(result.Inventory.Plugins);

        Assert.Equal(plugin.Id, restored.PluginId);
        Assert.NotNull(restored.Values);
        Assert.NotNull(restored.Files);
        Assert.DoesNotContain(
            "TOP_SECRET",
            JsonSerializer.Serialize(restored),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplicaSnapshot_RejectsPluginLogicalPathTraversal()
    {
        using SnapshotTestContext snapshotContext = new();
        string snapshotPath = Path.Combine(snapshotContext.RootPath, "unsafe.replica");
        ReplicaSnapshotWriteRequest request = snapshotContext.CreateRequest(
            snapshotPath,
            SnapshotType.Lightweight);
        ReplicaPluginSnapshot unsafePlugin = new(
            "replica.developer.test",
            "1.0.0",
            ["Capture"],
            [],
            new Dictionary<string, string>(),
            [new ReplicaPluginFile("../credential.json", "{}", "application/json")],
            []);
        request = request with
        {
            Inventory = request.Inventory with { Plugins = [unsafePlugin] },
        };

        await Assert.ThrowsAsync<ReplicaSnapshotException>(() =>
            snapshotContext.CreateWriter().WriteAsync(request, null, CancellationToken.None));

        Assert.False(File.Exists(snapshotPath));
    }

    [Fact]
    public void Catalog_ContainsAllOfficialPluginsWithoutExternalInstallation()
    {
        IReadOnlyList<IBuiltInPlugin> plugins = BuiltInPluginCatalog.CreateDefault();

        Assert.Equal(12, plugins.Count);
        Assert.Equal(12, plugins.Select(plugin => plugin.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(plugins, plugin => Assert.Equal("1.0.0", plugin.Version));
    }

    [Theory]
    [InlineData("../escape.json")]
    [InlineData("C:/absolute.json")]
    [InlineData("settings\\nested.json")]
    public void ToReplicaSnapshot_RejectsInvalidPluginArtifactPath(string logicalPath)
    {
        PluginSnapshot snapshot = new(
            "built-in.test",
            "1.0.0",
            new Dictionary<string, string>(),
            [new PluginCapturedFile(logicalPath, "safe content")],
            [],
            []);

        Assert.Throws<InvalidDataException>(snapshot.ToReplicaSnapshot);
    }

    private static IBuiltInPlugin CreatePlugin(string fixtureName)
    {
        return fixtureName switch
        {
            "vscode" => new VisualStudioCodePlugin(),
            "git" => new GitPlugin(),
            "powershell" => new PowerShellPlugin(),
            "terminal" => new WindowsTerminalPlugin(),
            "node" => new NodeJsPlugin(),
            "python" => new PythonPlugin(),
            _ => throw new ArgumentOutOfRangeException(nameof(fixtureName), fixtureName, null),
        };
    }

    private static PluginSnapshot EmptySnapshot(IBuiltInPlugin plugin) => new(
        plugin.Id,
        plugin.Version,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        [],
        [],
        []);

    private sealed class FixtureDeveloperPluginHost : IDeveloperPluginHost
    {
        private const string Root = "C:/ReplicaFixture";
        private readonly HashSet<string> _directories;
        private readonly Dictionary<string, string> _files;
        private readonly Dictionary<DeveloperToolQuery, string> _queries;

        private FixtureDeveloperPluginHost(
            Dictionary<DeveloperToolQuery, string> queries,
            Dictionary<string, string> files,
            HashSet<string> directories)
        {
            _queries = queries;
            _files = files;
            _directories = directories;
        }

        public static FixtureDeveloperPluginHost Load(string name)
        {
            string path = Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "DeveloperPlugins",
                $"{name}.json");
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            Dictionary<DeveloperToolQuery, string> queries = [];
            foreach (JsonProperty property in document.RootElement.GetProperty("queries").EnumerateObject())
            {
                queries[Enum.Parse<DeveloperToolQuery>(property.Name)] = property.Value.GetString()!;
            }

            Dictionary<string, string> files = new(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in document.RootElement.GetProperty("files").EnumerateObject())
            {
                files[Expand(property.Name)] = property.Value.GetString()!;
            }

            HashSet<string> directories = new(StringComparer.OrdinalIgnoreCase);
            if (document.RootElement.TryGetProperty("directories", out JsonElement directoryArray))
            {
                foreach (JsonElement directory in directoryArray.EnumerateArray())
                {
                    directories.Add(Expand(directory.GetString()!));
                }
            }

            return new FixtureDeveloperPluginHost(queries, files, directories);
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
            string normalized = Normalize(path);
            return _directories.Contains(normalized) ||
                _files.Keys.Any(file => file.StartsWith($"{normalized}/", StringComparison.OrdinalIgnoreCase));
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
                .Where(file => searchPattern == "*" ||
                    Path.GetFileName(file).Equals(searchPattern, StringComparison.OrdinalIgnoreCase) ||
                    searchPattern.StartsWith("*.", StringComparison.Ordinal) &&
                    file.EndsWith(searchPattern[1..], StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public BuiltInApplicationInfo GetApplicationInfo(BuiltInApplication application) =>
            new(false, null, false);

        public Task<DeveloperToolQueryResult> QueryAsync(
            DeveloperToolQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool found = _queries.TryGetValue(query, out string? output);
            string? version = found
                ? output!.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0]
                : null;
            return Task.FromResult(new DeveloperToolQueryResult(
                found,
                output ?? string.Empty,
                version,
                []));
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
