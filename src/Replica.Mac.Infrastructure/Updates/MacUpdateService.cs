using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using Replica.Core.Updates;

namespace Replica.Mac.Infrastructure.Updates;

public sealed record MacUpdateInfo(
    bool IsUpdateAvailable,
    string CurrentVersion,
    string? LatestVersion,
    string? ReleaseName,
    Uri? ReleasePage,
    Uri? DownloadUrl,
    long? DownloadSize,
    string Message);

public interface IMacUpdateService
{
    Task<MacUpdateInfo> CheckAsync(
        string currentVersion,
        UpdateChannel channel,
        CancellationToken cancellationToken);
}

public sealed class MacUpdateService : IMacUpdateService
{
    private static readonly Uri ReleasesEndpoint = new("https://api.github.com/repos/HechoLP/Replica/releases?per_page=20");
    private const int MaximumResponseBytes = 2 * 1024 * 1024;
    private readonly HttpClient client;

    public MacUpdateService(HttpClient client)
    {
        this.client = client;
    }

    public async Task<MacUpdateInfo> CheckAsync(
        string currentVersion,
        UpdateChannel channel,
        CancellationToken cancellationToken)
    {
        if (!SemanticVersion.TryParse(currentVersion, out SemanticVersion? current) || current is null)
        {
            return new MacUpdateInfo(false, currentVersion, null, null, null, null, null, "현재 버전을 확인할 수 없습니다.");
        }

        using HttpRequestMessage request = new(HttpMethod.Get, ReleasesEndpoint);
        request.Headers.UserAgent.ParseAdd("Replica-macOS/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using HttpResponseMessage response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Forbidden && response.Headers.Contains("X-RateLimit-Remaining"))
        {
            return new MacUpdateInfo(false, currentVersion, null, null, null, null, null, "GitHub API 조회 한도에 도달했습니다.");
        }

        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
        {
            throw new InvalidDataException("The GitHub release response is too large.");
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream bounded = new();
        await CopyBoundedAsync(stream, bounded, MaximumResponseBytes, cancellationToken).ConfigureAwait(false);
        bounded.Position = 0;
        using JsonDocument document = await JsonDocument.ParseAsync(
            bounded,
            new JsonDocumentOptions { MaxDepth = 32 },
            cancellationToken).ConfigureAwait(false);

        ReleaseCandidate? latest = document.RootElement.EnumerateArray()
            .Select(ParseRelease)
            .Where(candidate => candidate is not null && IsAllowed(candidate.Version, channel))
            .Select(candidate => candidate!)
            .OrderByDescending(candidate => candidate.Version)
            .FirstOrDefault();
        if (latest is null || latest.Version.CompareTo(current) <= 0)
        {
            return new MacUpdateInfo(false, currentVersion, latest?.Tag, latest?.Name, latest?.Page, null, null, "사용 가능한 새 버전이 없습니다.");
        }

        string architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        string expectedAsset = $"Replica-macOS-{architecture}.dmg";
        ReleaseAsset? asset = latest.Assets.FirstOrDefault(item => item.Name.Equals(expectedAsset, StringComparison.Ordinal));
        return new MacUpdateInfo(
            true,
            currentVersion,
            latest.Tag,
            latest.Name,
            latest.Page,
            asset?.DownloadUrl,
            asset?.Size,
            asset is null ? "새 버전이 있지만 이 Mac용 설치 파일이 없습니다." : "Replica 새 버전이 있습니다.");
    }

    private static ReleaseCandidate? ParseRelease(JsonElement release)
    {
        if (release.ValueKind != JsonValueKind.Object ||
            !release.TryGetProperty("draft", out JsonElement draftElement) ||
            draftElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            draftElement.GetBoolean() ||
            !release.TryGetProperty("tag_name", out JsonElement tagElement) ||
            tagElement.ValueKind != JsonValueKind.String ||
            !release.TryGetProperty("html_url", out JsonElement pageElement) ||
            pageElement.ValueKind != JsonValueKind.String ||
            !release.TryGetProperty("assets", out JsonElement assetsElement) ||
            assetsElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? tag = tagElement.GetString();
        if (!SemanticVersion.TryParse(tag, out SemanticVersion? version) || version is null)
        {
            return null;
        }

        Uri? page = Uri.TryCreate(pageElement.GetString(), UriKind.Absolute, out Uri? parsedPage) &&
            parsedPage.Scheme == Uri.UriSchemeHttps && parsedPage.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            ? parsedPage
            : null;
        List<ReleaseAsset> assets = [];
        foreach (JsonElement item in assetsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("name", out JsonElement nameElement) ||
                !item.TryGetProperty("browser_download_url", out JsonElement urlElement) ||
                !item.TryGetProperty("size", out JsonElement sizeElement) ||
                nameElement.ValueKind != JsonValueKind.String ||
                urlElement.ValueKind != JsonValueKind.String ||
                !sizeElement.TryGetInt64(out long size))
            {
                continue;
            }

            string? name = nameElement.GetString();
            string? url = urlElement.GetString();
            if (!string.IsNullOrWhiteSpace(name) &&
                size >= 0 &&
                Uri.TryCreate(url, UriKind.Absolute, out Uri? downloadUrl) &&
                downloadUrl.Scheme == Uri.UriSchemeHttps &&
                downloadUrl.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            {
                assets.Add(new ReleaseAsset(name, downloadUrl, size));
            }
        }

        return new ReleaseCandidate(
            version,
            tag!,
            release.TryGetProperty("name", out JsonElement nameProperty) && nameProperty.ValueKind == JsonValueKind.String
                ? nameProperty.GetString() ?? tag!
                : tag!,
            page,
            assets);
    }

    private static bool IsAllowed(SemanticVersion version, UpdateChannel channel)
    {
        return channel switch
        {
            UpdateChannel.Stable => version.Channel == UpdateChannel.Stable,
            UpdateChannel.Beta => version.Channel is UpdateChannel.Stable or UpdateChannel.Beta,
            UpdateChannel.Alpha => true,
            _ => false,
        };
    }

    private static async Task CopyBoundedAsync(
        Stream source,
        Stream destination,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[32 * 1024];
        int total = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            total = checked(total + read);
            if (total > maximumBytes)
            {
                throw new InvalidDataException("The GitHub release response is too large.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record ReleaseCandidate(
        SemanticVersion Version,
        string Tag,
        string Name,
        Uri? Page,
        IReadOnlyList<ReleaseAsset> Assets);

    private sealed record ReleaseAsset(string Name, Uri DownloadUrl, long Size);
}
