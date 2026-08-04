using Replica.Core.Scanning;
using Replica.Infrastructure.Scanning;

namespace Replica.Infrastructure.Tests.Scanning;

public sealed class EnvironmentVariableScannerTests
{
    [Fact]
    public async Task ScanAsync_PreservesPathOrderAndMarksDuplicates()
    {
        FakeEnvironmentValueSource source = new()
        {
            MachineValues = new Dictionary<string, string>
            {
                ["Path"] = "C:\\Tools;C:\\Missing",
            },
            UserValues = new Dictionary<string, string>
            {
                ["Path"] = "c:\\tools\\;C:\\UserTools",
            },
        };

        EnvironmentVariableScanResult result = await new EnvironmentVariableScanner(source)
            .ScanAsync(CancellationToken.None);

        Assert.Collection(
            result.PathEntries,
            entry =>
            {
                Assert.Equal("Machine", entry.Scope);
                Assert.Equal(0, entry.Order);
                Assert.False(entry.IsDuplicate);
            },
            entry => Assert.Equal(1, entry.Order),
            entry =>
            {
                Assert.Equal("User", entry.Scope);
                Assert.Equal(0, entry.Order);
                Assert.True(entry.IsDuplicate);
            },
            entry => Assert.Equal(1, entry.Order));
    }

    [Fact]
    public async Task ScanAsync_ExcludesSensitiveValuesButRecordsReason()
    {
        FakeEnvironmentValueSource source = new()
        {
            UserValues = new Dictionary<string, string>
            {
                ["SERVICE_TOKEN"] = "do-not-record",
                ["NORMAL_SETTING"] = "visible",
                ["DATABASE_CONNECTION_STRING"] = "secret-connection",
            },
        };

        EnvironmentVariableScanResult result = await new EnvironmentVariableScanner(source)
            .ScanAsync(CancellationToken.None);

        Assert.Equal(2, result.SensitiveExclusionCount);
        ScannedEnvironmentVariable token = result.Variables.Single(
            variable => variable.Name == "SERVICE_TOKEN");
        Assert.Null(token.Value);
        Assert.True(token.IsSensitive);
        Assert.Equal("SensitiveName:TOKEN", token.ExclusionReason);
        Assert.DoesNotContain(
            result.Variables,
            variable => variable.Value is "do-not-record" or "secret-connection");
        Assert.Equal(
            "visible",
            result.Variables.Single(variable => variable.Name == "NORMAL_SETTING").Value);
    }
}
