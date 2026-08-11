namespace Replica.IntegrationTests;

public sealed class MainWindowAccessibilityContractTests
{
    [Fact]
    public void NavigationHost_ExposesTheSelectedPageInsteadOfHidingItBehindATabPeer()
    {
        string shell = File.ReadAllText(RepositoryFile("src", "Replica.App", "MainWindow.xaml"));

        Assert.DoesNotContain("<TabControl", shell, StringComparison.Ordinal);
        Assert.Contains("<ContentControl Grid.Row=\"1\"", shell, StringComparison.Ordinal);
        Assert.Contains("Content=\"{Binding}\"", shell, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"HomePageTemplate\"", shell, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"SettingsPageTemplate\"", shell, StringComparison.Ordinal);
    }

    private static string RepositoryFile(params string[] segments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Replica.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine([directory!.FullName, .. segments]);
    }
}
