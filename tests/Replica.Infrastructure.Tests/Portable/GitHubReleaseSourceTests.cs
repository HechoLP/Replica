using System.Net;
using System.Security.Cryptography;
using System.Text;
using Replica.Core.Models;
using Replica.Core.Portable;
using Replica.Core.Services;
using Replica.Infrastructure.Portable;
using Replica.Infrastructure.Updates;

namespace Replica.Infrastructure.Tests.Portable;

public sealed class GitHubReleaseSourceTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "Replica-GitHub-Tests",
        Guid.NewGuid().ToString("N"));

    public GitHubReleaseSourceTests()
    {
        Directory.CreateDirectory(root);
    }

    [Fact]
    public async Task DownloadAsync_UsesMockedGitHubApiAndOfficialAssetOnly()
    {
        byte[] installer = Encoding.UTF8.GetBytes("mocked GitHub installer");
        string hash = Convert.ToHexString(SHA256.HashData(installer));
        string json = $$"""
            [
              {
                "tag_name": "v2.0.0",
                "html_url": "https://github.com/HechoLP/Replica/releases/tag/v2.0.0",
                "prerelease": false,
                "draft": false,
                "published_at": "2026-08-10T00:00:00Z",
                "assets": [
                  {
                    "name": "ReplicaSetup.exe",
                    "browser_download_url": "https://github.com/HechoLP/Replica/releases/download/v2.0.0/ReplicaSetup.exe",
                    "size": {{installer.Length}},
                    "digest": "sha256:{{hash.ToLowerInvariant()}}"
                  },
                  {
                    "name": "ReplicaSetup.exe",
                    "browser_download_url": "https://example.com/ReplicaSetup.exe",
                    "size": {{installer.Length}},
                    "digest": "sha256:{{hash.ToLowerInvariant()}}"
                  }
                ]
              }
            ]
            """;
        using HttpClient client = new(new FakeGitHubHandler(json, installer));
        GitHubReleaseSource source = new(client, ReleaseRepositoryOptions.Replica, TimeProvider.System);
        ReplicaInstallerDownloadService service = new(
            source,
            new WritableStorageInspector(root),
            new FileHashService());

        ReplicaInstallerDownloadResult result = await service.DownloadAsync(
            new ReplicaInstallerDownloadRequest(
                root,
                ReplicaReleaseSelection.LatestStable,
                null,
                UserApproved: true),
            null,
            CancellationToken.None);

        Assert.Equal("v2.0.0", result.VersionTag);
        Assert.Equal(hash, result.Sha256);
        Assert.True(result.HashWasProvidedByRelease);
        Assert.Equal(installer, await File.ReadAllBytesAsync(result.InstallerPath));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private sealed class FakeGitHubHandler(string releasesJson, byte[] installer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HttpResponseMessage response;
            if (request.RequestUri?.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) == true)
            {
                response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(releasesJson, Encoding.UTF8, "application/json"),
                };
            }
            else
            {
                response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(installer),
                };
            }

            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private sealed class WritableStorageInspector(string directory) : IPortableStorageInspector
    {
        public Task<PortableStorageInspection> InspectAsync(
            string directoryPath,
            long requiredBytes,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new PortableStorageInspection(
                directory,
                PortableStorageKind.FixedDrive,
                "NTFS",
                long.MaxValue,
                null,
                true,
                null,
                []));
        }
    }
}
