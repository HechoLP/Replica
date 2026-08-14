using Replica.Core.Plugins;
using Replica.Core.Scanning;
using Replica.Core.Services;
using Replica.Infrastructure.Scanning;

namespace Replica.Infrastructure.Tests.Scanning;

public sealed class EnvironmentScannerTests
{
    [Fact]
    public async Task ScanAsync_PropagatesCancellation()
    {
        CancelableWindowsScanner windowsScanner = new();
        EnvironmentScanner scanner = new(
            windowsScanner,
            new FakeApplicationScanner(),
            new FakeEnvironmentScanner(),
            new FakeFontScanner(),
            new FakePluginHost(),
            []);
        using CancellationTokenSource cancellation = new();
        Task<EnvironmentScanResult> scan = scanner.ScanAsync(null, cancellation.Token);
        await windowsScanner.Started.Task;

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => scan);
    }

    [Fact]
    public async Task ScanAsync_PreservesUsefulResultsAfterProviderFailure()
    {
        EnvironmentScanner scanner = new(
            new FailingWindowsScanner(),
            new FakeApplicationScanner(),
            new FakeEnvironmentScanner(),
            new FakeFontScanner(),
            new FakePluginHost(),
            [new FakePlugin()]);

        EnvironmentScanResult result = await scanner.ScanAsync(null, CancellationToken.None);

        Assert.Null(result.Windows);
        Assert.Single(result.Applications);
        Assert.Single(result.EnvironmentVariables);
        Assert.Single(result.Fonts);
        Assert.Equal(["test.plugin"], result.BuiltInPluginIds);
        PluginSnapshot snapshot = Assert.Single(result.BuiltInPluginSnapshots!);
        Assert.Equal("captured", snapshot.Values["setting"]);
        Assert.Single(snapshot.Exclusions);
        Assert.Contains(result.Warnings, warning => warning.Code == "WindowsInfoFailed");
        Assert.Contains(EnvironmentScanStage.WindowsInformation, result.IncompleteStages!);
        Assert.False(result.IsRestorePlanningComplete);
        Assert.Equal(result.Warnings.Count, result.Summary.WarningCount);
    }

    [Fact]
    public async Task ScanAsync_PreservesOtherResultsWhenPluginCaptureFails()
    {
        EnvironmentScanner scanner = new(
            new FailingWindowsScanner(),
            new FakeApplicationScanner(),
            new FakeEnvironmentScanner(),
            new FakeFontScanner(),
            new FakePluginHost(),
            [new FakePlugin(failCapture: true)]);

        EnvironmentScanResult result = await scanner.ScanAsync(null, CancellationToken.None);

        Assert.Empty(result.BuiltInPluginIds);
        Assert.Empty(result.BuiltInPluginSnapshots!);
        Assert.Contains(result.Warnings, warning =>
            warning.Provider == "Plugin:test.plugin" && warning.Code == "PluginCaptureFailed");
        Assert.DoesNotContain(EnvironmentScanStage.BuiltInPlugins, result.IncompleteStages!);
    }

    private sealed class CancelableWindowsScanner : IWindowsInfoScanner
    {
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<WindowsEnvironmentInfo> ScanAsync(
            CancellationToken cancellationToken)
        {
            Started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancellable delay should not complete.");
        }
    }

    private sealed class FailingWindowsScanner : IWindowsInfoScanner
    {
        public Task<WindowsEnvironmentInfo> ScanAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Simulated provider failure.");
        }
    }

    private sealed class FakeApplicationScanner : IApplicationScanner
    {
        public Task<ApplicationScanResult> ScanAsync(
            IProgress<EnvironmentScanProgress>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                new ApplicationScanResult(
                    [new ScannedApplication("App", "1", null, null, null, "Test", "User", "x64", "Test", false)],
                    0,
                    []));
        }
    }

    private sealed class FakeEnvironmentScanner : IEnvironmentVariableScanner
    {
        public Task<EnvironmentVariableScanResult> ScanAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                new EnvironmentVariableScanResult(
                    [new ScannedEnvironmentVariable("NORMAL", "value", "User", false, null)],
                    [],
                    0,
                    []));
        }
    }

    private sealed class FakeFontScanner : IFontScanner
    {
        public Task<FontScanResult> ScanAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                new FontScanResult(
                    [new ScannedFont("Font", "Regular", "User", "C:\\Font.ttf", false)],
                    []));
        }
    }

    private sealed class FakePlugin(bool failCapture = false) : IBuiltInPlugin
    {
        public string Id => "test.plugin";

        public string DisplayName => "Test plugin";

        public string Version => "1.0.0";

        public Task<PluginDetectionResult> DetectAsync(
            PluginCaptureContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new PluginDetectionResult(true, Version, ["PartialFixture"]));
        }

        public Task<PluginSnapshot> CaptureAsync(
            PluginCaptureContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (failCapture)
            {
                throw new IOException("Fixture failure with details that must not be exposed.");
            }

            return Task.FromResult(new PluginSnapshot(
                Id,
                Version,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["setting"] = "captured",
                },
                [new PluginCapturedFile("settings/config.json", "{}", "application/json")],
                [new PluginSensitiveExclusion("credentials/token", "CredentialExcluded", "Excluded fixture credential.")],
                ["CapturePartialFixture"]));
        }

        public Task<PluginComparisonResult> CompareAsync(
            PluginSnapshot source,
            PluginSnapshot target,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<PluginRestoreAction>> BuildRestoreActionsAsync(
            PluginComparisonResult comparison,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PluginValidationResult> ValidateAsync(
            PluginSnapshot expected,
            PluginCaptureContext context,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<PluginSensitiveExclusion>> GetSensitiveExclusionsAsync(
            PluginCaptureContext context,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakePluginHost : IDeveloperPluginHost
    {
        public string GetKnownPath(DeveloperKnownPath path) => "C:\\Fixture";

        public bool FileExists(string path) => false;

        public bool DirectoryExists(string path) => false;

        public long? GetFileSize(string path) => null;

        public Task<string?> ReadTextFileAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);

        public IReadOnlyList<string> EnumerateFiles(string path, string searchPattern, bool recursive) => [];

        public BuiltInApplicationInfo GetApplicationInfo(BuiltInApplication application) => new(false, null, false);

        public Task<DeveloperToolQueryResult> QueryAsync(
            DeveloperToolQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DeveloperToolQueryResult(false, string.Empty));
    }
}
