using Replica.Core.Platforms;
using Replica.Core.Services;
using Replica.Core.Snapshots;
using Replica.Mac.Infrastructure.Scanning;
using MacSensitiveEnvironmentPolicy = Replica.Mac.Infrastructure.Scanning.SensitiveEnvironmentPolicy;

namespace Replica.Mac.Tests;

public sealed class MacScannerTests
{
    [Theory]
    [InlineData("GITHUB_TOKEN")]
    [InlineData("DATABASE_PASSWORD")]
    [InlineData("AWS_SECRET_ACCESS_KEY")]
    [InlineData("CONNECTION_STRING")]
    [InlineData("DOCKER_AUTH_CONFIG")]
    [InlineData("CI_JOB_JWT")]
    [InlineData("NPM_CONFIG__AUTH")]
    public void SensitiveEnvironmentNamesAreRecognized(string name)
    {
        Assert.True(MacSensitiveEnvironmentPolicy.IsSensitive(name));
    }

    [Theory]
    [InlineData("LANG", true)]
    [InlineData("LC_CTYPE", true)]
    [InlineData("SHELL", true)]
    [InlineData("DATABASE_URL", false)]
    [InlineData("AWS_REGION", false)]
    [InlineData("HOME", false)]
    public void EnvironmentValuesRequireAnExplicitSafeName(string name, bool expected)
    {
        Assert.Equal(expected, MacSensitiveEnvironmentPolicy.CanCaptureValue(name));
    }

    [Fact]
    public async Task ApplicationBundleMetadataIsReadWithoutExecutingTheApplication()
    {
        using TemporaryDirectory temporary = new();
        string bundle = Path.Combine(temporary.Path, "Example.app", "Contents");
        Directory.CreateDirectory(bundle);
        await File.WriteAllTextAsync(
            Path.Combine(bundle, "Info.plist"),
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "https://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0"><dict>
              <key>CFBundleName</key><string>Example</string>
              <key>CFBundleIdentifier</key><string>com.example.replica-test</string>
              <key>CFBundleShortVersionString</key><string>1.2.3</string>
              <key>CFBundleExecutable</key><string>Example</string>
            </dict></plist>
            """);
        string executableDirectory = Path.Combine(bundle, "MacOS");
        Directory.CreateDirectory(executableDirectory);
        await File.WriteAllBytesAsync(
            Path.Combine(executableDirectory, "Example"),
            [0xCF, 0xFA, 0xED, 0xFE, 0x07, 0x00, 0x00, 0x01]);
        MacApplicationSource source = new([temporary.Path]);

        IReadOnlyList<PlatformApplication> applications = await source.ReadAsync(CancellationToken.None);

        PlatformApplication application = Assert.Single(applications);
        Assert.Equal("Example", application.Name);
        Assert.Equal("1.2.3", application.Version);
        Assert.Equal("com.example.replica-test", application.BundleIdentifier);
        Assert.Equal("x64", application.Architecture);
        Assert.False(application.IsRestorable);
    }

    [Fact]
    public async Task NestedApplicationBundlesAreInventoriedWithoutDescendingIntoBundles()
    {
        using TemporaryDirectory temporary = new();
        string contents = Path.Combine(temporary.Path, "Utilities", "Nested.app", "Contents");
        Directory.CreateDirectory(contents);
        await File.WriteAllTextAsync(
            Path.Combine(contents, "Info.plist"),
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0"><dict>
              <key>CFBundleName</key><string>Nested</string>
              <key>CFBundleIdentifier</key><string>com.example.nested</string>
            </dict></plist>
            """);
        MacApplicationSource source = new([temporary.Path]);

        PlatformApplication application = Assert.Single(
            await source.ReadAsync(CancellationToken.None));

        Assert.Equal("Nested", application.Name);
        Assert.Equal("com.example.nested", application.BundleIdentifier);
    }

    [Fact]
    public async Task BinaryPropertyListUsesTheBoundedTypedConverter()
    {
        using TemporaryDirectory temporary = new();
        string path = Path.Combine(temporary.Path, "Info.plist");
        await File.WriteAllBytesAsync(path, "bplist00fixture"u8.ToArray());
        FakeBinaryPropertyListConverter converter = new();
        MacPropertyListReader reader = new(converter);

        IReadOnlyDictionary<string, string> values = await reader.ReadStringDictionaryAsync(
            path,
            CancellationToken.None);

        Assert.Equal("Binary Example", values["CFBundleName"]);
        Assert.Equal(Path.GetFullPath(path), converter.ReceivedPath);
    }

    [Fact]
    public async Task ScannerPreservesPartialResultsWhenOneProviderFails()
    {
        MacEnvironmentScanner scanner = new(
            new FakeSystemSource(),
            new ThrowingApplicationSource(),
            new FakeHomebrewSource(),
            new FakeEnvironmentSource(),
            new FakeFontSource());

        PlatformScanResult result = await scanner.ScanAsync(progress: null, CancellationToken.None);

        Assert.Single(result.Applications);
        Assert.Equal(1, result.Summary.HomebrewPackageCount);
        Assert.Contains(result.Warnings, warning => warning.Provider == "ApplicationBundles");
        Assert.Single(result.Fonts);
    }

    private sealed class FakeSystemSource : IMacSystemInfoSource
    {
        public Task<(ReplicaPlatformInfo Platform, string MachineName)> ReadAsync(CancellationToken cancellationToken)
        {
            ReplicaPlatformInfo platform = new(
                ReplicaPlatformFamily.MacOS,
                "macOS",
                "15.0",
                "24A1",
                "Arm64",
                "ko-KR",
                "Asia/Seoul",
                ["MacOS"]);
            return Task.FromResult((platform, "TestMac"));
        }
    }

    private sealed class ThrowingApplicationSource : IMacApplicationSource
    {
        public Task<IReadOnlyList<PlatformApplication>> ReadAsync(CancellationToken cancellationToken)
        {
            throw new IOException("fixture failure");
        }
    }

    private sealed class FakeHomebrewSource : IHomebrewInventorySource
    {
        public Task<HomebrewInventoryResult> ReadAsync(CancellationToken cancellationToken)
        {
            PlatformApplication application = new(
                "git", "2.0", "Homebrew", null, null, "git", "HomebrewFormula", "Arm64", true);
            return Task.FromResult(new HomebrewInventoryResult([application], []));
        }
    }

    private sealed class FakeEnvironmentSource : IMacEnvironmentSource
    {
        public Task<MacEnvironmentResult> ReadAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(new MacEnvironmentResult(
                [new PlatformEnvironmentVariable("GITHUB_TOKEN", null, true, "SensitiveName")],
                []));
        }
    }

    private sealed class FakeFontSource : IMacFontSource
    {
        public Task<IReadOnlyList<PlatformFont>> ReadAsync(CancellationToken cancellationToken)
        {
            IReadOnlyList<PlatformFont> fonts = [new PlatformFont("Inter", "Regular", "User", "/tmp/Inter.ttf")];
            return Task.FromResult(fonts);
        }
    }

    private sealed class FakeBinaryPropertyListConverter : IMacBinaryPropertyListConverter
    {
        public string? ReceivedPath { get; private set; }

        public Task<byte[]> ConvertToXmlAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReceivedPath = path;
            return Task.FromResult(
                """
                <?xml version="1.0" encoding="UTF-8"?>
                <plist version="1.0"><dict>
                  <key>CFBundleName</key><string>Binary Example</string>
                </dict></plist>
                """u8.ToArray());
        }
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        string root = OperatingSystem.IsMacOS()
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : System.IO.Path.GetTempPath();
        Path = System.IO.Path.Combine(root, $".ReplicaMacTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
