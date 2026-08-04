using Replica.Core.Models;
using Replica.Core.Services;
using Replica.Infrastructure.Updates;

namespace Replica.Infrastructure.Tests;

public sealed class UpdateCheckServiceTests
{
    [Fact]
    public async Task CheckForUpdatesAsync_UsesProviderAndReportsNewerRelease()
    {
        FakeReleaseProvider provider = new(
            new ReleaseInfo(
                new Version(2, 0, 0),
                "v2.0.0",
                new Uri("https://github.com/HechoLP/Replica/releases/tag/v2.0.0"),
                false));
        UpdateCheckService service = new(
            new FakeAppVersionService(new Version(1, 0, 0)),
            provider,
            new ReleaseVersionComparer());

        UpdateCheckResult result = await service.CheckForUpdatesAsync(
            false,
            CancellationToken.None);

        Assert.True(provider.WasCalled);
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal("v2.0.0", result.LatestRelease?.TagName);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_ReturnsUnavailableWhenProviderHasNoRelease()
    {
        UpdateCheckService service = new(
            new FakeAppVersionService(new Version(1, 0, 0)),
            new FakeReleaseProvider(null),
            new ReleaseVersionComparer());

        UpdateCheckResult result = await service.CheckForUpdatesAsync(
            false,
            CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.Unavailable, result.Status);
        Assert.Null(result.LatestRelease);
    }

    private sealed class FakeAppVersionService : IAppVersionService
    {
        public FakeAppVersionService(Version version)
        {
            CurrentVersion = version;
        }

        public Version CurrentVersion { get; }

        public string DisplayVersion => CurrentVersion.ToString(3);
    }

    private sealed class FakeReleaseProvider : IReleaseProvider
    {
        private readonly ReleaseInfo? _release;

        public FakeReleaseProvider(ReleaseInfo? release)
        {
            _release = release;
        }

        public bool WasCalled { get; private set; }

        public Task<ReleaseInfo?> GetLatestReleaseAsync(
            bool includePrerelease,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WasCalled = true;
            return Task.FromResult(_release);
        }
    }
}
