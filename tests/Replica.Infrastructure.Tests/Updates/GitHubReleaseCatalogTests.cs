using System.Net;
using System.Text;
using Replica.Core.Models;
using Replica.Core.Updates;
using Replica.Infrastructure.Updates;

namespace Replica.Infrastructure.Tests.Updates;

public sealed class GitHubReleaseCatalogTests
{
    [Fact]
    public async Task GetCatalogAsync_ParsesReleaseMetadataAndSha256Digest()
    {
        string digest = new('A', 64);
        string json = $$"""
            [
              {
                "tag_name": "v1.1.0-beta.1",
                "name": "Replica Beta",
                "body": "Release notes",
                "html_url": "https://github.com/HechoLP/Replica/releases/tag/v1.1.0-beta.1",
                "prerelease": true,
                "draft": false,
                "published_at": "2026-08-10T00:00:00Z",
                "assets": [
                  {
                    "name": "ReplicaSetup.exe",
                    "browser_download_url": "https://github.com/HechoLP/Replica/releases/download/v1.1.0-beta.1/ReplicaSetup.exe",
                    "size": 123,
                    "digest": "sha256:{{digest.ToLowerInvariant()}}"
                  }
                ]
              }
            ]
            """;
        using HttpClient client = new(new ResponseHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            }));
        GitHubReleaseSource source = new(
            client,
            ReleaseRepositoryOptions.Replica,
            TimeProvider.System);

        GitHubReleaseCatalogResult result = await source.GetCatalogAsync(CancellationToken.None);

        Assert.Equal(GitHubReleaseCatalogStatus.Success, result.Status);
        GitHubReleaseDetails release = Assert.Single(result.Releases);
        Assert.Equal("Replica Beta", release.ReleaseName);
        Assert.Equal("Release notes", release.ReleaseNotes);
        Assert.Equal(UpdateChannel.Beta, release.Version.Channel);
        Assert.Equal(digest, Assert.Single(release.Assets).Sha256);
    }

    [Fact]
    public async Task GetCatalogAsync_ReportsRateLimitReset()
    {
        DateTimeOffset reset = DateTimeOffset.Parse(
            "2026-08-10T10:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture);
        HttpResponseMessage response = new(HttpStatusCode.TooManyRequests);
        response.Headers.Add("X-RateLimit-Remaining", "0");
        response.Headers.Add("X-RateLimit-Reset", reset.ToUnixTimeSeconds().ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        using HttpClient client = new(new ResponseHandler(response));
        GitHubReleaseSource source = new(
            client,
            ReleaseRepositoryOptions.Replica,
            TimeProvider.System);

        GitHubReleaseCatalogResult result = await source.GetCatalogAsync(CancellationToken.None);

        Assert.Equal(GitHubReleaseCatalogStatus.RateLimited, result.Status);
        Assert.Equal(reset, result.RetryAtUtc);
    }

    [Fact]
    public async Task GetCatalogAsync_ReportsInvalidJson()
    {
        using HttpClient client = new(new ResponseHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{ invalid", Encoding.UTF8, "application/json"),
            }));
        GitHubReleaseSource source = new(
            client,
            ReleaseRepositoryOptions.Replica,
            TimeProvider.System);

        GitHubReleaseCatalogResult result = await source.GetCatalogAsync(CancellationToken.None);

        Assert.Equal(GitHubReleaseCatalogStatus.InvalidResponse, result.Status);
        Assert.Empty(result.Releases);
    }

    [Fact]
    public async Task GetCatalogAsync_ReportsNetworkFailure()
    {
        using HttpClient client = new(new ThrowingHandler());
        GitHubReleaseSource source = new(
            client,
            ReleaseRepositoryOptions.Replica,
            TimeProvider.System);

        GitHubReleaseCatalogResult result = await source.GetCatalogAsync(CancellationToken.None);

        Assert.Equal(GitHubReleaseCatalogStatus.NetworkUnavailable, result.Status);
    }

    private sealed class ResponseHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => throw new HttpRequestException("offline");
    }
}
