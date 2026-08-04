using Replica.Core.Plugins;

namespace Replica.Plugins.BuiltIn;

public static class BuiltInPluginCatalog
{
    public static IReadOnlyList<IBuiltInPlugin> CreateDefault()
    {
        return
        [
            new VisualStudioCodePlugin(),
            new GitPlugin(),
            new PowerShellPlugin(),
            new WindowsTerminalPlugin(),
            new NodeJsPlugin(),
            new PythonPlugin(),
            new PowerToysPlugin(),
            new EverythingPlugin(),
            new ObsStudioPlugin(),
            new MinecraftPlugin(),
            new DockerDesktopPlugin(),
            new AbletonLivePlugin(),
        ];
    }
}
