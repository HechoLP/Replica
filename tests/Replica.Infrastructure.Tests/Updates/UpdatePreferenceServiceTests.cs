using Replica.Core.Updates;
using Replica.Infrastructure.Paths;
using Replica.Infrastructure.Updates;

namespace Replica.Infrastructure.Tests.Updates;

public sealed class UpdatePreferenceServiceTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "Replica-Update-Preference-Tests",
        Guid.NewGuid().ToString("N"));

    public UpdatePreferenceServiceTests()
    {
        Directory.CreateDirectory(root);
    }

    [Fact]
    public async Task SaveAsync_PersistsChannelAndSkippedVersionAtomically()
    {
        ReplicaPathProvider paths = new(root);
        UpdatePreferenceService service = new(paths);

        await service.SaveAsync(
            new UpdatePreference(UpdateChannel.Beta, "v1.1.0-beta.1"),
            CancellationToken.None);
        UpdatePreference result = await service.GetAsync(CancellationToken.None);

        Assert.Equal(UpdateChannel.Beta, result.Channel);
        Assert.Equal("v1.1.0-beta.1", result.SkippedVersionTag);
        Assert.Empty(Directory.EnumerateFiles(paths.ApplicationDataDirectory, "*.tmp"));
    }

    [Fact]
    public async Task GetAsync_ReturnsStableDefaultForInvalidSettings()
    {
        ReplicaPathProvider paths = new(root);
        Directory.CreateDirectory(paths.ApplicationDataDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(paths.ApplicationDataDirectory, "update-settings.json"),
            "{ invalid");
        UpdatePreferenceService service = new(paths);

        UpdatePreference result = await service.GetAsync(CancellationToken.None);

        Assert.Equal(UpdatePreference.Default, result);
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
}
