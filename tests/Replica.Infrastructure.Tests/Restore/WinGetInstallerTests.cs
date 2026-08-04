using Replica.Core.Execution;
using Replica.Core.Matching;
using Replica.Core.Scanning;
using Replica.Core.Services;
using Replica.Infrastructure.Restore;

namespace Replica.Infrastructure.Tests.Restore;

public sealed class WinGetInstallerTests
{
    [Fact]
    public async Task InstallAsync_ReturnsSuccessAfterPostInstallVerification()
    {
        FakeRestoreProcessRunner process = new(new ProcessExecutionResult(
            0,
            "installed",
            string.Empty,
            false,
            false));
        FakeApplicationScanner scanner = new(
            Scan(),
            Scan(Application("Git.Git", "2.51.0")));
        WinGetInstaller installer = CreateInstaller(process, scanner);

        WinGetInstallResult result = await installer.InstallAsync(
            Request(),
            CancellationToken.None);

        Assert.Equal(RestoreExecutionState.Succeeded, result.State);
        Assert.Equal("PackageInstalledAndVerified", result.ReasonCode);
        ProcessRequest request = Assert.Single(process.Requests);
        Assert.Equal(ProcessOperation.WinGetInstall, request.Operation);
        Assert.Equal("Git.Git", request.PackageIdentifier);
    }

    [Fact]
    public async Task InstallAsync_ReturnsFailureForNonzeroExitCode()
    {
        FakeRestoreProcessRunner process = new(new ProcessExecutionResult(
            7,
            string.Empty,
            "failed",
            false,
            false));
        WinGetInstaller installer = CreateInstaller(process, new FakeApplicationScanner(Scan()));

        WinGetInstallResult result = await installer.InstallAsync(Request(), CancellationToken.None);

        Assert.Equal(RestoreExecutionState.Failed, result.State);
        Assert.Equal("WinGetInstallFailed", result.ReasonCode);
        Assert.Equal(7, result.ExitCode);
    }

    [Fact]
    public async Task InstallAsync_ReturnsTimeoutWithoutVerification()
    {
        FakeRestoreProcessRunner process = new(new ProcessExecutionResult(
            null,
            string.Empty,
            string.Empty,
            true,
            false));
        WinGetInstaller installer = CreateInstaller(process, new FakeApplicationScanner(Scan()));

        WinGetInstallResult result = await installer.InstallAsync(Request(), CancellationToken.None);

        Assert.Equal(RestoreExecutionState.Failed, result.State);
        Assert.Equal("WinGetInstallTimeout", result.ReasonCode);
        Assert.True(result.TimedOut);
    }

    [Fact]
    public async Task InstallAsync_HonorsCancellationBeforeSystemWork()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        FakeRestoreProcessRunner process = new(new ProcessExecutionResult(
            0,
            string.Empty,
            string.Empty,
            false,
            false));
        WinGetInstaller installer = CreateInstaller(process, new FakeApplicationScanner(Scan()));

        await Assert.ThrowsAsync<OperationCanceledException>(() => installer.InstallAsync(
            Request(),
            cancellation.Token));

        Assert.Empty(process.Requests);
    }

    [Fact]
    public async Task InstallAsync_FailsWhenRescanCannotVerifyPackage()
    {
        FakeRestoreProcessRunner process = new(new ProcessExecutionResult(
            0,
            string.Empty,
            string.Empty,
            false,
            false));
        WinGetInstaller installer = CreateInstaller(
            process,
            new FakeApplicationScanner(Scan(), Scan()));

        WinGetInstallResult result = await installer.InstallAsync(Request(), CancellationToken.None);

        Assert.Equal(RestoreExecutionState.Failed, result.State);
        Assert.Equal("PackageVerificationFailed", result.ReasonCode);
    }

    [Fact]
    public async Task InstallAsync_SkipsAlreadyInstalledPackage()
    {
        FakeRestoreProcessRunner process = new(new ProcessExecutionResult(
            0,
            string.Empty,
            string.Empty,
            false,
            false));
        WinGetInstaller installer = CreateInstaller(
            process,
            new FakeApplicationScanner(Scan(Application("Git.Git", "2.51.0"))));

        WinGetInstallResult result = await installer.InstallAsync(Request(), CancellationToken.None);

        Assert.Equal(RestoreExecutionState.Skipped, result.State);
        Assert.Equal("PackageAlreadyInstalled", result.ReasonCode);
        Assert.Empty(process.Requests);
    }

    [Theory]
    [InlineData(ApplicationMatchConfidence.Low)]
    [InlineData(ApplicationMatchConfidence.Unknown)]
    public async Task InstallAsync_BlocksLowConfidenceIdentity(ApplicationMatchConfidence confidence)
    {
        FakeRestoreProcessRunner process = new(new ProcessExecutionResult(
            0,
            string.Empty,
            string.Empty,
            false,
            false));
        WinGetInstaller installer = CreateInstaller(process, new FakeApplicationScanner(Scan()));

        WinGetInstallResult result = await installer.InstallAsync(
            Request() with { Confidence = confidence },
            CancellationToken.None);

        Assert.Equal(RestoreExecutionState.Failed, result.State);
        Assert.Equal("LowConfidenceBlocked", result.ReasonCode);
        Assert.Empty(process.Requests);
    }

    private static WinGetInstaller CreateInstaller(
        IProcessRunner process,
        IApplicationScanner scanner)
    {
        return new WinGetInstaller(process, scanner, new ApplicationVersionComparer());
    }

    private static WinGetInstallRequest Request()
    {
        return new WinGetInstallRequest(
            "Git.Git",
            "2.51.0",
            ApplicationMatchConfidence.Exact,
            false,
            TimeSpan.FromMinutes(5));
    }

    private static ApplicationScanResult Scan(params ScannedApplication[] applications)
    {
        return new ApplicationScanResult(applications, applications.Length, []);
    }

    private static ScannedApplication Application(string packageId, string version)
    {
        return new ScannedApplication(
            packageId,
            version,
            "Test",
            null,
            packageId,
            "Test",
            "User",
            "x64",
            "WinGet",
            true);
    }

    private sealed class FakeRestoreProcessRunner(ProcessExecutionResult result) : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];

        public bool IsToolAvailable(ProcessTool tool) => true;

        public Task<ProcessExecutionResult> RunAsync(
            ProcessRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(result);
        }
    }

    private sealed class FakeApplicationScanner(params ApplicationScanResult[] results) : IApplicationScanner
    {
        private int _index;

        public Task<ApplicationScanResult> ScanAsync(
            IProgress<EnvironmentScanProgress>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplicationScanResult result = results[Math.Min(_index, results.Length - 1)];
            _index++;
            return Task.FromResult(result);
        }
    }
}
