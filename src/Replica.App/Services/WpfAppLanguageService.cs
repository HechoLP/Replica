using System.Globalization;
using System.Windows;

namespace Replica.App.Services;

public sealed class WpfAppLanguageService : IAppLanguageService
{
    private static readonly HashSet<string> SupportedLanguages =
        new(StringComparer.OrdinalIgnoreCase) { "ko-KR", "en-US" };

    public string CurrentLanguageCode { get; private set; } = "ko-KR";

    public void ApplyLanguage(string languageCode)
    {
        if (!SupportedLanguages.Contains(languageCode))
        {
            throw new ArgumentOutOfRangeException(nameof(languageCode));
        }

        CultureInfo culture = CultureInfo.GetCultureInfo(languageCode);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Application? application = Application.Current;
        if (application is not null)
        {
            ResourceDictionary? existing = application.Resources.MergedDictionaries
                .FirstOrDefault(dictionary => dictionary.Source?.OriginalString.Contains(
                    "/Localization/",
                    StringComparison.OrdinalIgnoreCase) == true);
            int index = existing is null
                ? 0
                : application.Resources.MergedDictionaries.IndexOf(existing);
            if (existing is not null)
            {
                application.Resources.MergedDictionaries.Remove(existing);
            }

            application.Resources.MergedDictionaries.Insert(
                index,
                new ResourceDictionary
                {
                    Source = new Uri(
                        $"/Replica;component/Resources/Localization/Strings.{languageCode}.xaml",
                        UriKind.Relative),
                });
        }

        CurrentLanguageCode = languageCode;
    }
}
