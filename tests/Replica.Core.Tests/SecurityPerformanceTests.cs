using System.Diagnostics;
using Replica.Core.Diffing;
using Replica.Core.Matching;

namespace Replica.Core.Tests;

public sealed class SecurityPerformanceTests
{
    [Fact]
    [Trait("Category", "Performance")]
    public void ApplicationMatcher_HandlesThousandsOfStableIdentitiesDeterministically()
    {
        const int applicationCount = 2_000;
        ApplicationDescriptor[] source = Enumerable.Range(0, applicationCount)
            .Select(index => Application(index))
            .ToArray();
        ApplicationMatcher matcher = new(new ApplicationIdentityNormalizer());
        Stopwatch stopwatch = Stopwatch.StartNew();

        ApplicationMatchingResult first = matcher.Match(source, []);
        ApplicationMatchingResult second = matcher.Match(source, []);

        stopwatch.Stop();
        Assert.Equal(applicationCount, first.UnmatchedSource.Count);
        Assert.Equal(
            first.UnmatchedSource.Select(item => item.Application.WingetPackageId),
            second.UnmatchedSource.Select(item => item.Application.WingetPackageId));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), stopwatch.Elapsed.ToString());
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void DiffEngine_HandlesTensOfThousandsOfFileManifestItems()
    {
        const int itemCount = 20_000;
        DiffValueEntry[] source = Enumerable.Range(0, itemCount)
            .Select(index => FileValue(index, index == itemCount - 1 ? "A" : "SAME"))
            .ToArray();
        DiffValueEntry[] target = Enumerable.Range(0, itemCount)
            .Select(index => FileValue(index, index == itemCount - 1 ? "B" : "SAME"))
            .ToArray();
        EnvironmentDiffEngine engine = new(
            new ApplicationMatcher(new ApplicationIdentityNormalizer()),
            new ApplicationVersionComparer(),
            new ApplicationAutomationPolicy());
        Stopwatch stopwatch = Stopwatch.StartNew();

        EnvironmentDiffResult result = engine.Compare(
            new DiffEnvironmentState([], [], source, []),
            new DiffEnvironmentState([], [], target, []),
            DiffRestoreMode.Safe);

        stopwatch.Stop();
        Assert.Equal(itemCount, result.Items.Count);
        Assert.Equal(itemCount - 1, result.Items.Count(item => item.Type == DiffType.ExactMatch));
        DiffItem changed = Assert.Single(result.Items, item => item.Type == DiffType.FileChanged);
        Assert.Equal(DiffType.FileChanged, changed.Type);
        Assert.Equal("files/19999.json", changed.Key);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), stopwatch.Elapsed.ToString());
    }

    private static ApplicationDescriptor Application(int index) => new(
        $"Application {index:D4}",
        "1.0.0",
        "Replica Performance Fixture",
        $"C:\\Apps\\{index:D4}",
        "x64",
        $"Fixture.Application{index:D4}",
        null,
        null);

    private static DiffValueEntry FileValue(int index, string hash) => new(
        DiffArea.SelectedUserFiles,
        $"files/{index:D5}.json",
        $"File {index:D5}",
        null,
        hash,
        IsSupported: true,
        IsSensitiveExcluded: false,
        CanRestoreAutomatically: true);
}
