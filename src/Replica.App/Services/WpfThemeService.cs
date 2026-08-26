using System.IO;
using System.Text.Json;
using System.Windows;
using Replica.Core.Services;
using ReplicaThemeMode = Replica.Core.Services.ThemeMode;

namespace Replica.App.Services;

public sealed class WpfThemeService : IThemeService
{
    private readonly string settingsPath;

    public WpfThemeService(IReplicaPathProvider paths)
    {
        settingsPath = Path.Combine(paths.ApplicationDataDirectory, "appearance.json");
        CurrentTheme = LoadTheme();
    }

    public ReplicaThemeMode CurrentTheme { get; private set; }

    public void ApplyTheme(ReplicaThemeMode theme)
    {
        Application? application = Application.Current;
        if (application is not null)
        {
            ResourceDictionary? existingTheme = application.Resources.MergedDictionaries
                .FirstOrDefault(dictionary => dictionary.Source?.OriginalString.Contains(
                    "/Themes/",
                    StringComparison.OrdinalIgnoreCase) == true);

            if (existingTheme is not null)
            {
                application.Resources.MergedDictionaries.Remove(existingTheme);
            }

            string themeResource = SystemParameters.HighContrast ? "HighContrast" : theme.ToString();
            application.Resources.MergedDictionaries.Add(
                new ResourceDictionary
                {
                    Source = new Uri($"/Replica;component/Themes/{themeResource}.xaml", UriKind.Relative),
                });
        }

        CurrentTheme = theme;
        TrySaveTheme(theme);
    }

    private ReplicaThemeMode LoadTheme()
    {
        try
        {
            FileInfo file = new(settingsPath);
            if (!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file.Length > 4096)
            {
                return ReplicaThemeMode.Light;
            }

            AppearanceSettings? settings = JsonSerializer.Deserialize<AppearanceSettings>(
                File.ReadAllText(settingsPath));
            return settings is not null && Enum.IsDefined(settings.Theme)
                ? settings.Theme
                : ReplicaThemeMode.Light;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return ReplicaThemeMode.Light;
        }
    }

    private void TrySaveTheme(ReplicaThemeMode theme)
    {
        string? temporaryPath = null;
        try
        {
            string directory = Path.GetDirectoryName(settingsPath)!;
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(directory, $".appearance.{Guid.NewGuid():N}.tmp");
            using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, new AppearanceSettings(theme));
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(settingsPath))
            {
                File.Replace(temporaryPath, settingsPath, null);
            }
            else
            {
                File.Move(temporaryPath, settingsPath, overwrite: false);
            }

            temporaryPath = null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Applying the theme is still safe when its optional preference cannot be persisted.
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Best-effort cleanup for an optional preference write.
                }
            }
        }
    }

    private sealed record AppearanceSettings(ReplicaThemeMode Theme);
}
