using System.Windows;
using Replica.Core.Services;
using ReplicaThemeMode = Replica.Core.Services.ThemeMode;

namespace Replica.App.Services;

public sealed class WpfThemeService : IThemeService
{
    public ReplicaThemeMode CurrentTheme { get; private set; } = ReplicaThemeMode.Light;

    public void ApplyTheme(ReplicaThemeMode theme)
    {
        Application? application = Application.Current;
        if (application is null)
        {
            return;
        }

        ResourceDictionary? existingTheme = application.Resources.MergedDictionaries
            .FirstOrDefault(dictionary => dictionary.Source?.OriginalString.Contains(
                "/Themes/",
                StringComparison.OrdinalIgnoreCase) == true);

        if (existingTheme is not null)
        {
            application.Resources.MergedDictionaries.Remove(existingTheme);
        }

        application.Resources.MergedDictionaries.Add(
            new ResourceDictionary
            {
                Source = new Uri($"/Replica;component/Themes/{theme}.xaml", UriKind.Relative),
            });

        CurrentTheme = theme;
    }
}
