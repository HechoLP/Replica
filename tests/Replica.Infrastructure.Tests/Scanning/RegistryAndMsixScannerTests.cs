using Replica.Core.Scanning;
using Replica.Infrastructure.Scanning;

namespace Replica.Infrastructure.Tests.Scanning;

public sealed class RegistryAndMsixScannerTests
{
    [Fact]
    public async Task RegistryScanner_DeduplicatesProductCodeAcrossRegistryViews()
    {
        const string productCode = "{DB9A8EC2-AB7A-4837-9378-A4327B90CF6B}";
        RegistryApplicationRecord[] records =
        [
            new(productCode, "Example App", "1.0", "Example", null, "Machine", "x86", true),
            new(productCode, "Example App", "1.0", "Example", "C:\\Example", "Machine", "x64", true),
        ];
        RegistryApplicationScanner scanner = new(new FakeRegistryApplicationSource(records));

        RegistryApplicationScanResult result = await scanner.ScanAsync(CancellationToken.None);

        ScannedApplication application = Assert.Single(result.Applications);
        Assert.Equal("x64", application.Architecture);
        Assert.Equal("C:\\Example", application.InstallLocation);
        Assert.Equal("MSI", application.InstallType);
        Assert.Equal("db9a8ec2-ab7a-4837-9378-a4327b90cf6b", application.MsiProductCode);
    }

    [Fact]
    public async Task MsixScanner_SeparatesFrameworkPackages()
    {
        const string json = """
            [
              {
                "Name": "Contoso.App",
                "Version": "1.2.3.4",
                "Publisher": "CN=Contoso",
                "Architecture": "X64",
                "InstallLocation": "C:\\Program Files\\WindowsApps\\Contoso.App",
                "PackageFamilyName": "Contoso.App_123",
                "IsFramework": false
              },
              {
                "Name": "Contoso.Framework",
                "Version": "1.0.0.0",
                "Architecture": "X64",
                "PackageFamilyName": "Contoso.Framework_123",
                "IsFramework": true
              }
            ]
            """;
        FakeProcessRunner runner = new();
        runner.SetResult(
            ProcessOperation.MsixInventory,
            new ProcessExecutionResult(0, json, string.Empty, false, false));

        MsixApplicationScanResult result = await new MsixApplicationScanner(runner)
            .ScanAsync(CancellationToken.None);

        Assert.Equal(2, result.Applications.Count);
        ScannedApplication framework = Assert.Single(
            result.Applications,
            application => application.IsFramework);
        Assert.Equal("MSIX Framework", framework.InstallType);
        Assert.False(framework.IsRestorable);
        Assert.True(result.Applications.Single(application => !application.IsFramework).IsRestorable);
    }
}
