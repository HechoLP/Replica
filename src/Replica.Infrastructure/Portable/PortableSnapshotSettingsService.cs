using System.Text.Json;
using Replica.Core.Portable;
using Replica.Core.Services;

namespace Replica.Infrastructure.Portable;

public sealed class PortableSnapshotSettingsService : IPortableSnapshotSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string settingsPath;

    public PortableSnapshotSettingsService(IReplicaPathProvider paths)
    {
        settingsPath = Path.Combine(paths.ApplicationDataDirectory, "portable-snapshot.json");
    }

    public async Task<PortableSnapshotSettings> GetAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(settingsPath))
            {
                return new PortableSnapshotSettings(null);
            }

            FileInfo file = new(settingsPath);
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file.Length > 64 * 1024)
            {
                throw new PortableSnapshotException("Portable snapshot settings are not safe to read.");
            }

            await using FileStream stream = new(
                settingsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            PortableSnapshotSettings? settings = await JsonSerializer.DeserializeAsync<PortableSnapshotSettings>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
            return settings ?? new PortableSnapshotSettings(null);
        }
        catch (PortableSnapshotException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new PortableSnapshotException("Portable snapshot settings could not be read safely.", exception);
        }
    }

    public async Task SaveDefaultDirectoryAsync(string directoryPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        string fullPath = Path.GetFullPath(directoryPath);
        if (!Directory.Exists(fullPath) || PortablePathSafety.ContainsReparsePoint(fullPath))
        {
            throw new PortableSnapshotException("The default snapshot directory is not safe.");
        }

        string directory = Path.GetDirectoryName(settingsPath)!;
        if (PortablePathSafety.ContainsReparsePoint(directory))
        {
            throw new PortableSnapshotException("The settings directory is not safe.");
        }

        Directory.CreateDirectory(directory);

        string temporaryPath = Path.Combine(directory, $".portable-snapshot.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    new PortableSnapshotSettings(fullPath),
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(settingsPath))
            {
                File.Replace(temporaryPath, settingsPath, null);
            }
            else
            {
                File.Move(temporaryPath, settingsPath, overwrite: false);
            }
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}
