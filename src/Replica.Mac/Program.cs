using Avalonia;

namespace Replica.Mac;

internal static class Program
{
    [STAThread]
    public static void Main(string[] arguments)
    {
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(arguments);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont();
    }
}
