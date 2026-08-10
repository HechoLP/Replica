using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Replica.Core.Models;
using Replica.Core.Portable;
using Replica.Core.Services;
using Replica.Core.Updates;

namespace Replica.Infrastructure.Updates;

public sealed class GitHubReleaseSource : IOfficialReleaseSource, IGitHubReleaseCatalog
{
    private const int MaximumReleaseResponseBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions ReleaseJsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32,
        PropertyNameCaseInsensitive = false,
    };
    private static readonly TimeSpan ReleaseLookupTimeout = TimeSpan.FromSeconds(30);
    private readonly HttpClient httpClient;
    private readonly ReleaseRepositoryOptions repository;
    private readonly TimeProvider timeProvider;

    public GitHubReleaseSource(
        HttpClient httpClient,
        ReleaseRepositoryOptions repository,
        TimeProvider timeProvider)
    {
        this.httpClient = httpClient;
        this.repository = repository;
        this.timeProvider = timeProvider;
        if (!repository.Owner.Equals("HechoLP", StringComparison.Ordinal) ||
            !repository.Repository.Equals("Replica", StringComparison.Ordinal))
        {
            throw new ArgumentException("Only the official Replica release repository is supported.", nameof(repository));
        }
    }

    public async Task<IReadOnlyList<OfficialReplicaRelease>> GetReleasesAsync(
        CancellationToken cancellationToken)
    {
        GitHubReleaseCatalogResult catalog = await GetCatalogAsync(cancellationToken).ConfigureAwait(false);
        if (catalog.Status != GitHubReleaseCatalogStatus.Success)
        {
            throw new ReplicaInstallerDownloadException(
                $"GitHub release metadata is unavailable ({catalog.Status}).");
        }

        return catalog.Releases.Select(release => new OfficialReplicaRelease(
                new Version(release.Version.Major, release.Version.Minor, release.Version.Patch),
                release.TagName,
                release.ReleasePage,
                release.IsPrerelease,
                release.IsDraft,
                release.PublishedAtUtc,
                release.Assets.Select(asset => new OfficialReleaseAsset(
                    asset.Name,
                    asset.DownloadUri,
                    asset.Size,
                    asset.Sha256)).ToArray()))
            .ToArray();
    }

    public async Task<GitHubReleaseCatalogResult> GetCatalogAsync(
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReleaseLookupTimeout);
        CancellationToken operationToken = timeout.Token;
        Uri requestUri = new(
            $"https://api.github.com/repos/{repository.Owner}/{repository.Repository}/releases?per_page=100");
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, requestUri);
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                operationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new GitHubReleaseCatalogResult(GitHubReleaseCatalogStatus.TimedOut, []);
        }
        catch (HttpRequestException)
        {
            return new GitHubReleaseCatalogResult(GitHubReleaseCatalogStatus.NetworkUnavailable, []);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                return new GitHubReleaseCatalogResult(
                    GitHubReleaseCatalogStatus.RateLimited,
                    [],
                    GetRetryAtUtc(response));
            }

            if (!response.IsSuccessStatusCode)
            {
                return new GitHubReleaseCatalogResult(GitHubReleaseCatalogStatus.ServiceUnavailable, []);
            }

            if (response.Content.Headers.ContentLength > MaximumReleaseResponseBytes)
            {
                return new GitHubReleaseCatalogResult(GitHubReleaseCatalogStatus.InvalidResponse, []);
            }

            Stream responseStream;
            try
            {
                responseStream = await response.Content.ReadAsStreamAsync(operationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new GitHubReleaseCatalogResult(GitHubReleaseCatalogStatus.TimedOut, []);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                return new GitHubReleaseCatalogResult(GitHubReleaseCatalogStatus.NetworkUnavailable, []);
            }

            await using (responseStream)
            {
                byte[] content;
                try
                {
                    content = await ReadBoundedAsync(
                        responseStream,
                        MaximumReleaseResponseBytes,
                        operationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return new GitHubReleaseCatalogResult(GitHubReleaseCatalogStatus.TimedOut, []);
                }
                catch (ReplicaInstallerDownloadException)
                {
                    return new GitHubReleaseCatalogResult(GitHubReleaseCatalogStatus.InvalidResponse, []);
                }
                catch (Exception exception) when (exception is HttpRequestException or IOException)
                {
                    return new GitHubReleaseCatalogResult(GitHubReleaseCatalogStatus.NetworkUnavailable, []);
                }

                GitHubReleaseDto[] releases;
                try
                {
                    releases = JsonSerializer.Deserialize<GitHubReleaseDto[]>(
                        content,
                        ReleaseJsonOptions) ?? [];
                }
                catch (JsonException)
                {
                    return new GitHubReleaseCatalogResult(GitHubReleaseCatalogStatus.InvalidResponse, []);
                }

                return new GitHubReleaseCatalogResult(
                    GitHubReleaseCatalogStatus.Success,
                    releases
                        .Select(MapRelease)
                        .Where(release => release is not null)
                        .Cast<GitHubReleaseDetails>()
                        .OrderByDescending(release => release.PublishedAtUtc)
                        .ToArray());
            }
        }
    }

    public async Task<Stream> OpenAssetStreamAsync(
        OfficialReleaseAsset asset,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ValidateOfficialAssetUri(asset.DownloadUri);
        HttpRequestMessage request = CreateRequest(HttpMethod.Get, asset.DownloadUri);
        HttpResponseMessage? response = null;
        try
        {
            response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            ValidateDownloadResponseUri(response.RequestMessage?.RequestUri);
            Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new OwnedResponseStream(stream, request, response);
        }
        catch
        {
            request.Dispose();
            response?.Dispose();
            throw;
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, Uri uri)
    {
        HttpRequestMessage request = new(method, uri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Replica", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    private GitHubReleaseDetails? MapRelease(GitHubReleaseDto release)
    {
        if (string.IsNullOrWhiteSpace(release.TagName) ||
            release.HtmlUrl is null ||
            release.PublishedAt is null ||
            !SemanticVersion.TryParse(release.TagName, out SemanticVersion? version) ||
            version is null ||
            version.IsPrerelease != release.Prerelease ||
            !IsOfficialReleasePage(release.HtmlUrl))
        {
            return null;
        }

        IReadOnlyList<GitHubReleaseAssetInfo> assets = (release.Assets ?? [])
            .Where(asset =>
                !string.IsNullOrWhiteSpace(asset.Name) &&
                asset.BrowserDownloadUrl is not null &&
                asset.Size is > 0)
            .Where(asset => IsOfficialAssetUri(asset.BrowserDownloadUrl!))
            .Select(asset => new GitHubReleaseAssetInfo(
                asset.Name!,
                asset.BrowserDownloadUrl!,
                asset.Size,
                ParseDigest(asset.Digest)))
            .ToArray();
        return new GitHubReleaseDetails(
            version!,
            release.TagName,
            string.IsNullOrWhiteSpace(release.Name) ? release.TagName : release.Name.Trim(),
            release.PublishedAt.Value,
            release.Prerelease,
            release.Draft,
            release.Body ?? string.Empty,
            release.HtmlUrl,
            assets);
    }

    private bool IsOfficialReleasePage(Uri uri) =>
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.StartsWith(
            $"/{repository.Owner}/{repository.Repository}/releases/",
            StringComparison.Ordinal);

    private bool IsOfficialAssetUri(Uri uri) =>
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.StartsWith(
            $"/{repository.Owner}/{repository.Repository}/releases/download/",
            StringComparison.Ordinal);

    private void ValidateOfficialAssetUri(Uri uri)
    {
        if (!IsOfficialAssetUri(uri))
        {
            throw new ReplicaInstallerDownloadException("The installer asset is not from the official Replica release.");
        }
    }

    private static void ValidateDownloadResponseUri(Uri? uri)
    {
        if (uri is null ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !(uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ReplicaInstallerDownloadException("The installer download redirected to an untrusted host.");
        }
    }

    private static string? ParseDigest(string? digest)
    {
        const string prefix = "sha256:";
        if (digest is null || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string value = digest[prefix.Length..];
        return value.Length == 64 && value.All(Uri.IsHexDigit) ? value.ToUpperInvariant() : null;
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using MemoryStream buffer = new();
        byte[] chunk = new byte[16 * 1024];
        while (true)
        {
            int read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > maximumBytes)
            {
                throw new ReplicaInstallerDownloadException("The GitHub release response exceeded the safety limit.");
            }

            buffer.Write(chunk, 0, read);
        }
    }

    private sealed record GitHubReleaseDto(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("html_url")] Uri? HtmlUrl,
        [property: JsonPropertyName("prerelease")] bool Prerelease,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("published_at")] DateTimeOffset? PublishedAt,
        [property: JsonPropertyName("assets")] GitHubAssetDto[]? Assets);

    private sealed record GitHubAssetDto(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("browser_download_url")] Uri? BrowserDownloadUrl,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("digest")] string? Digest);

    private DateTimeOffset? GetRetryAtUtc(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Date is DateTimeOffset retryDate)
        {
            return retryDate;
        }

        if (response.Headers.RetryAfter?.Delta is TimeSpan retryDelta)
        {
            return timeProvider.GetUtcNow().Add(retryDelta);
        }

        if (response.Headers.TryGetValues("X-RateLimit-Reset", out IEnumerable<string>? values) &&
            long.TryParse(values.FirstOrDefault(), out long unixSeconds))
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }

        return null;
    }

    private sealed class OwnedResponseStream : Stream
    {
        private readonly Stream inner;
        private readonly HttpRequestMessage request;
        private readonly HttpResponseMessage response;

        public OwnedResponseStream(Stream inner, HttpRequestMessage request, HttpResponseMessage response)
        {
            this.inner = inner;
            this.request = request;
            this.response = response;
        }

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
                request.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            response.Dispose();
            request.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
