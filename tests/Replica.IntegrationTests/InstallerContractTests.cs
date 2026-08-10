using System.Xml.Linq;

namespace Replica.IntegrationTests;

public sealed class InstallerContractTests
{
    [Fact]
    public void ApplicationPublish_IsSelfContainedSingleFileAndUntrimmed()
    {
        XDocument project = XDocument.Load(RepositoryFile("src", "Replica.App", "Replica.App.csproj"));
        IReadOnlyDictionary<string, string> properties = project
            .Descendants()
            .Where(element => !element.HasElements)
            .GroupBy(element => element.Name.LocalName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal);

        Assert.Equal("win-x64", properties["RuntimeIdentifier"]);
        Assert.Equal("true", properties["SelfContained"]);
        Assert.Equal("true", properties["PublishSingleFile"]);
        Assert.Equal("false", properties["PublishTrimmed"]);
        Assert.Equal("Replica", properties["AssemblyName"]);
        Assert.Equal("HechoLP", properties["Company"]);
    }

    [Fact]
    public void InnoSetup_InstallsOnlyReplicaAndPreservesUserDataByDefault()
    {
        string installer = File.ReadAllText(RepositoryFile("installer", "Replica.iss"));

        Assert.Contains("DefaultDirName={localappdata}\\Programs\\Replica", installer, StringComparison.Ordinal);
        Assert.Contains("PrivilegesRequired=lowest", installer, StringComparison.Ordinal);
        Assert.Contains("OutputBaseFilename=ReplicaSetup-{#ReplicaVersion}", installer, StringComparison.Ordinal);
        Assert.Contains("Source: \"{#PublishDirectory}\\Replica.exe\"", installer, StringComparison.Ordinal);
        Assert.Contains("--open-snapshot", installer, StringComparison.Ordinal);
        Assert.Contains("if UninstallSilent then", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("[UninstallDelete]", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("SignTool=", installer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("helper", installer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("service", installer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReleaseScripts_AvoidArbitraryCommandEvaluationAndVerifyHash()
    {
        string buildScript = File.ReadAllText(RepositoryFile("scripts", "build-release.ps1"));
        string testScript = File.ReadAllText(RepositoryFile("scripts", "test-installer.ps1"));

        Assert.Contains("dotnet", buildScript, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Get-FileHash", buildScript, StringComparison.Ordinal);
        Assert.Contains("Get-AuthenticodeSignature", testScript, StringComparison.Ordinal);
        Assert.Contains("preserve.marker", testScript, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-Expression", buildScript, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Invoke-Expression", testScript, StringComparison.OrdinalIgnoreCase);
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
