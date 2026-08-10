using System.Text.Json;
using Replica.Core.Services;
using Replica.Core.Updates;
using Replica.Infrastructure.Portable;

namespace Replica.Infrastructure.Updates;

public sealed class UpdatePreferenceService : IUpdatePreferenceService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string settingsPath;

    public UpdatePreferenceService(IReplicaPathProvider paths)
    {
        settingsPath = Path.Combine(paths.ApplicationDataDirectory, "update-settings.json");
    }

    public async Task<UpdatePreference> GetAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(settingsPath))
            {
                return UpdatePreference.Default;
            }

            FileInfo file = new(settingsPath);
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file.Length > 64 * 1024)
            {
                return UpdatePreference.Default;
            }

            await using FileStream stream = new(
                settingsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            UpdatePreference? preference = await JsonSerializer.DeserializeAsync<UpdatePreference>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
            return preference is not null && IsValid(preference)
                ? preference
                : UpdatePreference.Default;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return UpdatePreference.Default;
        }
    }

    public async Task SaveAsync(UpdatePreference preference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preference);
        if (!IsValid(preference))
        {
            throw new ArgumentException("The update preference is invalid.", nameof(preference));
        }

        string directory = Path.GetDirectoryName(settingsPath)!;
        if (PortablePathSafety.ContainsReparsePoint(directory))
        {
            throw new IOException("The update settings directory is unsafe.");
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, $".update-settings.{Guid.NewGuid():N}.tmp");
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
                    preference,
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
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Settings remain either on the prior complete file or the new complete file.
            }
        }
    }

    private static bool IsValid(UpdatePreference preference) =>
        Enum.IsDefined(preference.Channel) &&
        (preference.SkippedVersionTag is null ||
         (preference.SkippedVersionTag.Length <= 64 &&
          SemanticVersion.TryParse(preference.SkippedVersionTag, out _)));
}
