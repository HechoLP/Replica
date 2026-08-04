using Replica.Infrastructure.Updates;

namespace Replica.Infrastructure.Tests;

public sealed class AppVersionServiceTests
{
    [Fact]
    public void Constructor_ReadsVersionFromProvidedAssembly()
    {
        var assembly = typeof(AppVersionServiceTests).Assembly;

        AppVersionService service = new(assembly);

        Assert.Equal(assembly.GetName().Version, service.CurrentVersion);
        Assert.False(string.IsNullOrWhiteSpace(service.DisplayVersion));
    }
}
