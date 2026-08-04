using Replica.Core.Services;
using Replica.Infrastructure.Environment;

namespace Replica.Infrastructure.Tests;

public sealed class WindowsCompatibilityServiceTests
{
    [Fact]
    public void GetCompatibility_AcceptsWindows11Build()
    {
        WindowsCompatibilityService service = new(
            new FakeOperatingSystemInfo(true, new Version(10, 0, 22631)));

        var result = service.GetCompatibility();

        Assert.True(result.IsSupported);
        Assert.Contains("confirmed", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false, 10, 0, 22631)]
    [InlineData(true, 10, 0, 19045)]
    public void GetCompatibility_RejectsUnsupportedEnvironment(
        bool isWindows,
        int major,
        int minor,
        int build)
    {
        WindowsCompatibilityService service = new(
            new FakeOperatingSystemInfo(isWindows, new Version(major, minor, build)));

        var result = service.GetCompatibility();

        Assert.False(result.IsSupported);
        Assert.Contains("requires Windows 11", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakeOperatingSystemInfo : IOperatingSystemInfo
    {
        public FakeOperatingSystemInfo(bool isWindows, Version version)
        {
            IsWindows = isWindows;
            Version = version;
        }

        public bool IsWindows { get; }

        public Version Version { get; }

        public string Description => "Test operating system";
    }
}
