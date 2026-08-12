using Replica.Core.Platforms;
using Replica.Core.Services;
using Replica.Core.Snapshots;
using Replica.Mac.Infrastructure.Scanning;

namespace Replica.Mac.Tests;

public sealed class MacScannerTests
{
    [Theory]
    [InlineData("GITHUB_TOKEN")]
    [InlineData("DATABASE_PASSWORD")]
    [InlineData("AWS_SECRET_ACCESS_KEY")]
    [InlineData("CONNECTION_STRING")]
    public void SensitiveEnvironmentNamesAreRecognized(string name)
    {
        Assert.True(SensitiveEnvironmentPolicy.IsSensitive(name));
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
            </dict></plist>
            """);
        MacApplicationSource source = new([temporary.Path]);

        IReadOnlyList<PlatformApplication> applications = await source.ReadAsync(CancellationToken.None);

        PlatformApplication application = Assert.Single(applications);
        Assert.Equal("Example", application.Name);
        Assert.Equal("1.2.3", application.Version);
        Assert.Equal("com.example.replica-test", application.BundleIdentifier);
        Assert.False(application.IsRestorable);
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
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ReplicaMacTests-{Guid.NewGuid():N}");
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
