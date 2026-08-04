namespace Replica.Core.Services;

public enum ThemeMode
{
    Light,
    Dark,
}

public interface IThemeService
{
    ThemeMode CurrentTheme { get; }

    void ApplyTheme(ThemeMode theme);
}
