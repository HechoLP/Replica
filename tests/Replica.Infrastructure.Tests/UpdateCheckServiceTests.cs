using Replica.Core.Models;
using Replica.Core.Services;
using Replica.Core.Updates;
using Replica.Infrastructure.Updates;

namespace Replica.Infrastructure.Tests;

public sealed class UpdateCheckServiceTests
{
    [Fact]
    public async Task CheckForUpdatesAsync_ReportsLatestSemanticVersion()
    {
        UpdateCheckService service = CreateService(
            new Version(0, 1, 0),
            [Release("v0.2.0"), Release("v0.1.5")]);

        UpdateCheckResult result = await service.CheckForUpdatesAsync(
            UpdateChannel.Stable,
            CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal("v0.2.0", result.LatestRelease?.TagName);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_ReturnsUpToDateWhenNoNewerVersionExists()
    {
        UpdateCheckService service = CreateService(
            new Version(1, 0, 0),
            [Release("v1.0.0")]);

        UpdateCheckResult result = await service.CheckForUpdatesAsync(
            UpdateChannel.Stable,
            CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
    }

    [Theory]
    [InlineData(UpdateChannel.Stable, "v1.0.0")]
    [InlineData(UpdateChannel.Beta, "v1.1.0-rc.1")]
    [InlineData(UpdateChannel.Alpha, "v1.2.0-alpha.1")]
    public async Task CheckForUpdatesAsync_RespectsSelectedChannel(
        UpdateChannel channel,
        string expectedTag)
    {
        UpdateCheckService service = CreateService(
            new Version(0, 9, 0),
            [
                Release("v1.0.0"),
                Release("v1.1.0-beta.1"),
                Release("v1.1.0-rc.1"),
                Release("v1.2.0-alpha.1"),
            ]);

        UpdateCheckResult result = await service.CheckForUpdatesAsync(
            channel,
            CancellationToken.None);

        Assert.Equal(expectedTag, result.LatestRelease?.TagName);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_ExcludesDraftRelease()
    {
        UpdateCheckService service = CreateService(
            new Version(1, 0, 0),
            [Release("v9.0.0", draft: true), Release("v1.1.0")]);

        UpdateCheckResult result = await service.CheckForUpdatesAsync(
            UpdateChannel.Stable,
            CancellationToken.None);

        Assert.Equal("v1.1.0", result.LatestRelease?.TagName);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_ReportsRateLimitAndRetryTime()
    {
        DateTimeOffset retry = DateTimeOffset.Parse(
            "2026-08-10T10:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture);
        UpdateCheckService service = new(
            new FakeAppVersionService(new Version(1, 0, 0)),
            new FakeCatalog(new GitHubReleaseCatalogResult(
                GitHubReleaseCatalogStatus.RateLimited,
                [],
                retry)),
            new MemoryPreferenceService());

        UpdateCheckResult result = await service.CheckForUpdatesAsync(
            UpdateChannel.Stable,
            CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.RateLimited, result.Status);
        Assert.Equal(retry, result.RetryAtUtc);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_ReportsMissingInstallerAsset()
    {
        UpdateCheckService service = CreateService(
            new Version(1, 0, 0),
            [Release("v1.1.0", includeInstaller: false)]);

        UpdateCheckResult result = await service.CheckForUpdatesAsync(
            UpdateChannel.Stable,
            CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.AssetMissing, result.Status);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_HonorsSkippedVersion()
    {
        MemoryPreferenceService preference = new()
        {
            Value = new UpdatePreference(UpdateChannel.Stable, "v1.1.0"),
        };
        UpdateCheckService service = new(
            new FakeAppVersionService(new Version(1, 0, 0)),
            new FakeCatalog(new GitHubReleaseCatalogResult(
                GitHubReleaseCatalogStatus.Success,
                [Release("v1.1.0")])),
            preference);

        UpdateCheckResult result = await service.CheckForUpdatesAsync(
            UpdateChannel.Stable,
            CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.Skipped, result.Status);
    }

    private static UpdateCheckService CreateService(
        Version current,
        IReadOnlyList<GitHubReleaseDetails> releases) => new(
            new FakeAppVersionService(current),
            new FakeCatalog(new GitHubReleaseCatalogResult(
                GitHubReleaseCatalogStatus.Success,
                releases)),
            new MemoryPreferenceService());

    private static GitHubReleaseDetails Release(
        string tag,
        bool draft = false,
        bool includeInstaller = true)
    {
        Assert.True(SemanticVersion.TryParse(tag, out SemanticVersion? version));
        return new GitHubReleaseDetails(
            version!,
            tag,
            $"Replica {tag}",
            DateTimeOffset.Parse(
                "2026-08-10T00:00:00Z",
                System.Globalization.CultureInfo.InvariantCulture),
            version!.IsPrerelease,
            draft,
            "Release notes",
            new Uri($"https://github.com/HechoLP/Replica/releases/tag/{tag}"),
            includeInstaller
                ?
                [
                    new GitHubReleaseAssetInfo(
                        "ReplicaSetup.exe",
                        new Uri($"https://github.com/HechoLP/Replica/releases/download/{tag}/ReplicaSetup.exe"),
                        100,
                        null),
                ]
                : []);
    }

    private sealed class FakeAppVersionService(Version version) : IAppVersionService
    {
        public Version CurrentVersion { get; } = version;

        public string DisplayVersion => CurrentVersion.ToString(3);
    }

    private sealed class FakeCatalog(GitHubReleaseCatalogResult result) : IGitHubReleaseCatalog
    {
        public Task<GitHubReleaseCatalogResult> GetCatalogAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
    }

    private sealed class MemoryPreferenceService : IUpdatePreferenceService
    {
        public UpdatePreference Value { get; set; } = UpdatePreference.Default;

        public Task<UpdatePreference> GetAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Value);
        }

        public Task SaveAsync(UpdatePreference preference, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Value = preference;
            return Task.CompletedTask;
        }
    }
}
