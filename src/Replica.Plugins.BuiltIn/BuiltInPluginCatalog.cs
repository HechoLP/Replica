using Replica.Core.Plugins;

namespace Replica.Plugins.BuiltIn;

public static class BuiltInPluginCatalog
{
    public static IReadOnlyList<IBuiltInPlugin> CreateDefault()
    {
        return [new FoundationPlugin()];
    }

    private sealed class FoundationPlugin : IBuiltInPlugin
    {
        public string Id => "replica.foundation";

        public string DisplayName => "Replica built-in foundation";
    }
}
