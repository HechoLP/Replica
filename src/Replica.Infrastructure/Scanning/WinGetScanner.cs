using System.Text.Json;
using System.Text.RegularExpressions;
using Replica.Core.Scanning;
using Replica.Core.Services;

namespace Replica.Infrastructure.Scanning;

public sealed partial class WinGetScanner : IWinGetScanner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private readonly IProcessRunner _processRunner;

    public WinGetScanner(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public async Task<WinGetScanResult> ScanAsync(CancellationToken cancellationToken)
    {
        if (!_processRunner.IsToolAvailable(ProcessTool.WinGet))
        {
            return new WinGetScanResult(
                false,
                [],
                [new ScanWarning("WinGet", "WinGetUnavailable", "WinGet is not available.")]);
        }

        Dictionary<string, WinGetPackage> packages = new(StringComparer.OrdinalIgnoreCase);
        List<ScanWarning> warnings = [];

        ProcessExecutionResult export = await _processRunner.RunAsync(
            new ProcessRequest(ProcessOperation.WinGetExport, DefaultTimeout),
            cancellationToken).ConfigureAwait(false);
        AddProcessWarnings(export, "WinGetExport", warnings);
        if (export.ExitCode == 0 && !export.TimedOut)
        {
            TryParseExport(export.StandardOutput, packages, warnings);
        }

        ProcessExecutionResult list = await _processRunner.RunAsync(
            new ProcessRequest(ProcessOperation.WinGetList, DefaultTimeout, 2_097_152),
            cancellationToken).ConfigureAwait(false);
        AddProcessWarnings(list, "WinGetList", warnings);
        if (list.ExitCode == 0 && !list.TimedOut)
        {
            TryEnrichFromList(list.StandardOutput, packages, warnings);
        }

        return new WinGetScanResult(true, packages.Values.ToArray(), warnings);
    }

    private static void TryParseExport(
        string json,
        IDictionary<string, WinGetPackage> packages,
        ICollection<ScanWarning> warnings)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions { MaxDepth = 32 });
            if (!document.RootElement.TryGetProperty("Sources", out JsonElement sources) ||
                sources.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("Missing Sources array.");
            }

            foreach (JsonElement source in sources.EnumerateArray())
            {
                string? sourceName = GetNestedString(source, "SourceDetails", "Name");
                if (!source.TryGetProperty("Packages", out JsonElement sourcePackages) ||
                    sourcePackages.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (JsonElement package in sourcePackages.EnumerateArray())
                {
                    string? packageId = GetString(package, "PackageIdentifier");
                    if (string.IsNullOrWhiteSpace(packageId))
                    {
                        continue;
                    }

                    packages[packageId] = new WinGetPackage(
                        packageId,
                        GetString(package, "PackageName"),
                        GetString(package, "Version"),
                        sourceName);
                }
            }
        }
        catch (JsonException)
        {
            warnings.Add(new ScanWarning(
                "WinGet",
                "InvalidExport",
                "WinGet returned an invalid export document."));
        }
    }

    private static void TryEnrichFromList(
        string output,
        IDictionary<string, WinGetPackage> packages,
        ICollection<ScanWarning> warnings)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return;
        }

        int parsed = 0;
        foreach (string rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("-", StringComparison.Ordinal))
            {
                continue;
            }

            string[] columns = ColumnSeparator().Split(line);
            if (columns.Length < 3 ||
                columns[0].Equals("Name", StringComparison.OrdinalIgnoreCase) ||
                columns[1].Equals("Id", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string packageId = columns[1].Trim();
            if (!IsPlausiblePackageId(packageId))
            {
                continue;
            }

            string name = columns[0].Trim();
            string version = columns[2].Trim();
            string? source = columns.Length >= 5 ? columns[^1].Trim() : null;
            if (packages.TryGetValue(packageId, out WinGetPackage? existing))
            {
                packages[packageId] = existing with
                {
                    Name = string.IsNullOrWhiteSpace(existing.Name) ? name : existing.Name,
                    Version = string.IsNullOrWhiteSpace(existing.Version) ? version : existing.Version,
                    Source = string.IsNullOrWhiteSpace(existing.Source) ? source : existing.Source,
                };
            }
            else
            {
                packages[packageId] = new WinGetPackage(packageId, name, version, source);
            }

            parsed++;
        }

        if (parsed == 0 && packages.Count == 0)
        {
            warnings.Add(new ScanWarning(
                "WinGet",
                "UnrecognizedList",
                "WinGet list output could not be recognized."));
        }
    }

    private static void AddProcessWarnings(
        ProcessExecutionResult result,
        string operation,
        ICollection<ScanWarning> warnings)
    {
        if (result.TimedOut)
        {
            warnings.Add(new ScanWarning(
                "WinGet",
                $"{operation}Timeout",
                $"{operation} exceeded its time limit."));
        }
        else if (result.ExitCode != 0)
        {
            warnings.Add(new ScanWarning(
                "WinGet",
                $"{operation}Failed",
                $"{operation} did not complete successfully."));
        }

        if (result.OutputTruncated)
        {
            warnings.Add(new ScanWarning(
                "WinGet",
                $"{operation}OutputLimit",
                $"{operation} output exceeded the safe size limit."));
        }
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement property) &&
            property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static string? GetNestedString(
        JsonElement element,
        string objectName,
        string propertyName)
    {
        return element.TryGetProperty(objectName, out JsonElement nested) &&
            nested.ValueKind == JsonValueKind.Object
            ? GetString(nested, propertyName)
            : null;
    }

    private static bool IsPlausiblePackageId(string value)
    {
        return value.Length is > 1 and <= 255 &&
            value.Any(character => character is '.' or '_') &&
            value.All(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_');
    }

    [GeneratedRegex("\\s{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex ColumnSeparator();
}
