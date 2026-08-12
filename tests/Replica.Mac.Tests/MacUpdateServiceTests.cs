using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using Replica.Core.Updates;
using Replica.Mac.Infrastructure.Updates;

namespace Replica.Mac.Tests;

public sealed class MacUpdateServiceTests
{
    [Fact]
    public async Task SelectsTheCurrentMacArchitectureAssetFromOfficialRelease()
    {
        string architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        string json = $$"""
        [{
          "draft": false,
          "tag_name": "v0.2.0-alpha.1",
          "name": "Replica 0.2.0 Alpha 1",
          "html_url": "https://github.com/HechoLP/Replica/releases/tag/v0.2.0-alpha.1",
          "assets": [{
            "name": "Replica-macOS-{{architecture}}.dmg",
            "browser_download_url": "https://github.com/HechoLP/Replica/releases/download/v0.2.0-alpha.1/Replica-macOS-{{architecture}}.dmg",
            "size": 1234
          }]
        }]
        """;
        using HttpClient client = new(new JsonHandler(json));
        MacUpdateService service = new(client);

        MacUpdateInfo result = await service.CheckAsync(
            "0.1.0-alpha.1",
            UpdateChannel.Alpha,
            CancellationToken.None);

        Assert.True(result.IsUpdateAvailable);
        Assert.NotNull(result.DownloadUrl);
        Assert.EndsWith($"Replica-macOS-{architecture}.dmg", result.DownloadUrl.AbsolutePath, StringComparison.Ordinal);
    }

    private sealed class JsonHandler : HttpMessageHandler
    {
        private readonly string json;

        public JsonHandler(string json)
        {
            this.json = json;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal("api.github.com", request.RequestUri?.Host);
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            };
            return Task.FromResult(response);
        }
    }
}
