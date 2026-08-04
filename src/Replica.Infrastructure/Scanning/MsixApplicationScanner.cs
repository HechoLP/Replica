using System.Text.Json;
using Replica.Core.Scanning;
using Replica.Core.Services;

namespace Replica.Infrastructure.Scanning;

public sealed class MsixApplicationScanner : IMsixApplicationScanner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private readonly IProcessRunner _processRunner;

    public MsixApplicationScanner(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public async Task<MsixApplicationScanResult> ScanAsync(CancellationToken cancellationToken)
    {
        if (!_processRunner.IsToolAvailable(ProcessTool.PowerShell))
        {
            return new MsixApplicationScanResult(
                [],
                [new ScanWarning("MSIX", "PowerShellUnavailable", "PowerShell is not available.")]);
        }

        ProcessExecutionResult result = await _processRunner.RunAsync(
            new ProcessRequest(ProcessOperation.MsixInventory, DefaultTimeout, 2_097_152),
            cancellationToken).ConfigureAwait(false);
        if (result.TimedOut)
        {
            return new MsixApplicationScanResult(
                [],
                [new ScanWarning("MSIX", "MsixTimeout", "MSIX inventory exceeded its time limit.")]);
        }

        if (result.ExitCode != 0)
        {
            return new MsixApplicationScanResult(
                [],
                [new ScanWarning("MSIX", "MsixFailed", "MSIX inventory did not complete successfully.")]);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                result.StandardOutput,
                new JsonDocumentOptions { MaxDepth = 32 });
            IEnumerable<JsonElement> packages = document.RootElement.ValueKind switch
            {
                JsonValueKind.Array => document.RootElement.EnumerateArray(),
                JsonValueKind.Object => [document.RootElement],
                _ => throw new JsonException("Expected an object or array."),
            };

            List<ScannedApplication> applications = [];
            foreach (JsonElement package in packages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? name = GetString(package, "Name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                bool isFramework = GetBoolean(package, "IsFramework");
                applications.Add(new ScannedApplication(
                    name,
                    GetString(package, "Version"),
                    GetString(package, "Publisher"),
                    GetString(package, "InstallLocation"),
                    null,
                    "MSIX/Store",
                    "User",
                    GetString(package, "Architecture") ?? "Unknown",
                    isFramework ? "MSIX Framework" : "MSIX",
                    !isFramework,
                    isFramework,
                    GetString(package, "PackageFamilyName")));
            }

            List<ScanWarning> warnings = [];
            if (result.OutputTruncated)
            {
                warnings.Add(new ScanWarning(
                    "MSIX",
                    "MsixOutputLimit",
                    "MSIX inventory output exceeded the safe size limit."));
            }

            return new MsixApplicationScanResult(applications, warnings);
        }
        catch (JsonException)
        {
            return new MsixApplicationScanResult(
                [],
                [new ScanWarning("MSIX", "InvalidMsixOutput", "MSIX inventory returned invalid data.")]);
        }
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            _ => null,
        };
    }

    private static bool GetBoolean(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement property) &&
            property.ValueKind == JsonValueKind.True;
    }
}
