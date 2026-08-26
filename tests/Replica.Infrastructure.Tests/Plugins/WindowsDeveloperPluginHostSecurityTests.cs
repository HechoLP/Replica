using System.Reflection;
using Replica.Plugins.BuiltIn;

namespace Replica.Infrastructure.Tests.Plugins;

public sealed class WindowsDeveloperPluginHostSecurityTests : IDisposable
{
    private readonly string testDirectory = Path.Combine(
        Path.GetTempPath(),
        "Replica-DeveloperHost-Tests",
        Guid.NewGuid().ToString("N"));

    public WindowsDeveloperPluginHostSecurityTests()
    {
        Directory.CreateDirectory(testDirectory);
    }

    [Fact]
    public void ResolverIgnoresAttackerControlledPathEntries()
    {
        string fakeGit = Path.Combine(testDirectory, "git.exe");
        File.WriteAllBytes(fakeGit, "not an executable"u8.ToArray());
        string? originalPath = System.Environment.GetEnvironmentVariable("PATH");
        try
        {
            System.Environment.SetEnvironmentVariable("PATH", testDirectory);
            MethodInfo resolver = typeof(WindowsDeveloperPluginHost).GetMethod(
                "ResolveExecutable",
                BindingFlags.NonPublic | BindingFlags.Static)!;

            string? resolved = (string?)resolver.Invoke(null, ["git.exe"]);

            Assert.False(string.Equals(fakeGit, resolved, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    public void Dispose()
    {
        Directory.Delete(testDirectory, recursive: true);
    }
}
