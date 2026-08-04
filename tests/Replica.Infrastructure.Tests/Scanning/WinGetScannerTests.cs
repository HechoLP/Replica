using Replica.Core.Scanning;
using Replica.Infrastructure.Scanning;

namespace Replica.Infrastructure.Tests.Scanning;

public sealed class WinGetScannerTests
{
    [Fact]
    public async Task ScanAsync_ParsesFakeExportAndEnrichesFromList()
    {
        const string export = """
            {
              "Sources": [
                {
                  "SourceDetails": { "Name": "winget" },
                  "Packages": [
                    { "PackageIdentifier": "Git.Git", "Version": "2.50.0" }
                  ]
                }
              ]
            }
            """;
        const string list = """
            Name                    Id       Version  Available  Source
            ----------------------------------------------------------
            Git                     Git.Git  2.50.0   2.51.0     winget
            """;
        FakeProcessRunner runner = new();
        runner.SetResult(
            ProcessOperation.WinGetExport,
            new ProcessExecutionResult(0, export, string.Empty, false, false));
        runner.SetResult(
            ProcessOperation.WinGetList,
            new ProcessExecutionResult(0, list, string.Empty, false, false));

        WinGetScanResult result = await new WinGetScanner(runner)
            .ScanAsync(CancellationToken.None);

        WinGetPackage package = Assert.Single(result.Packages);
        Assert.Equal("Git.Git", package.PackageId);
        Assert.Equal("Git", package.Name);
        Assert.Equal("winget", package.Source);
        Assert.Empty(result.Warnings);
        Assert.Equal(
            [ProcessOperation.WinGetExport, ProcessOperation.WinGetList],
            runner.Operations);
    }

    [Fact]
    public async Task ScanAsync_WhenWinGetIsMissing_ReturnsWarningWithoutRunningProcess()
    {
        FakeProcessRunner runner = new() { WinGetAvailable = false };

        WinGetScanResult result = await new WinGetScanner(runner)
            .ScanAsync(CancellationToken.None);

        Assert.False(result.IsAvailable);
        Assert.Empty(result.Packages);
        Assert.Contains(result.Warnings, warning => warning.Code == "WinGetUnavailable");
        Assert.Empty(runner.Operations);
    }

    [Fact]
    public async Task ScanAsync_WhenExportTimesOut_ReportsPartialFailure()
    {
        FakeProcessRunner runner = new();
        runner.SetResult(
            ProcessOperation.WinGetExport,
            new ProcessExecutionResult(null, string.Empty, string.Empty, true, false));
        runner.SetResult(
            ProcessOperation.WinGetList,
            new ProcessExecutionResult(0, string.Empty, string.Empty, false, false));

        WinGetScanResult result = await new WinGetScanner(runner)
            .ScanAsync(CancellationToken.None);

        Assert.Contains(result.Warnings, warning => warning.Code == "WinGetExportTimeout");
    }
}
