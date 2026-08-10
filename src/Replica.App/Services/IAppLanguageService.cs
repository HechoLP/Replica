namespace Replica.App.Services;

public interface IAppLanguageService
{
    string CurrentLanguageCode { get; }

    void ApplyLanguage(string languageCode);
}
