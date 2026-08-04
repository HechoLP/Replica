using Replica.Core.Scanning;
using Replica.Core.Services;

namespace Replica.Infrastructure.Scanning;

public sealed class ApplicationScanner : IApplicationScanner
{
    private const int TotalStages = 7;
    private readonly IMsixApplicationScanner _msixScanner;
    private readonly IRegistryApplicationScanner _registryScanner;
    private readonly IWinGetScanner _winGetScanner;

    public ApplicationScanner(
        IWinGetScanner winGetScanner,
        IRegistryApplicationScanner registryScanner,
        IMsixApplicationScanner msixScanner)
    {
        _winGetScanner = winGetScanner;
        _registryScanner = registryScanner;
        _msixScanner = msixScanner;
    }

    public async Task<ApplicationScanResult> ScanAsync(
        IProgress<EnvironmentScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        List<ScanWarning> warnings = [];
        RegistryApplicationScanResult registry = new([], []);
        WinGetScanResult winGet = new(false, [], []);
        MsixApplicationScanResult msix = new([], []);

        progress?.Report(new EnvironmentScanProgress(
            EnvironmentScanStage.Applications,
            1,
            TotalStages,
            "프로그램 정보를 읽는 중입니다."));
        try
        {
            registry = await _registryScanner.ScanAsync(cancellationToken).ConfigureAwait(false);
            warnings.AddRange(registry.Warnings);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            warnings.Add(new ScanWarning(
                "Registry",
                "RegistryScanFailed",
                "Installed desktop applications could not be fully read."));
        }

        try
        {
            winGet = await _winGetScanner.ScanAsync(cancellationToken).ConfigureAwait(false);
            warnings.AddRange(winGet.Warnings);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            warnings.Add(new ScanWarning(
                "WinGet",
                "WinGetScanFailed",
                "WinGet inventory could not be read."));
        }

        progress?.Report(new EnvironmentScanProgress(
            EnvironmentScanStage.StoreApplications,
            2,
            TotalStages,
            "Store 앱 정보를 읽는 중입니다."));
        try
        {
            msix = await _msixScanner.ScanAsync(cancellationToken).ConfigureAwait(false);
            warnings.AddRange(msix.Warnings);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            warnings.Add(new ScanWarning(
                "MSIX",
                "MsixScanFailed",
                "MSIX and Store applications could not be fully read."));
        }

        List<ScannedApplication> applications = [.. registry.Applications, .. msix.Applications];
        MatchWinGetPackages(applications, winGet.Packages);
        applications = Deduplicate(applications);
        int matches = applications.Count(application => application.WinGetId is not null);

        return new ApplicationScanResult(applications, matches, warnings);
    }

    private static void MatchWinGetPackages(
        ICollection<ScannedApplication> applications,
        IReadOnlyList<WinGetPackage> packages)
    {
        HashSet<string> matchedIds = new(StringComparer.OrdinalIgnoreCase);
        List<ScannedApplication> updated = [];
        foreach (ScannedApplication application in applications)
        {
            WinGetPackage? match = packages.FirstOrDefault(package => IsMatch(application, package));
            if (match is null)
            {
                updated.Add(application);
                continue;
            }

            matchedIds.Add(match.PackageId);
            updated.Add(application with
            {
                WinGetId = match.PackageId,
                Source = $"{application.Source};WinGet",
                IsRestorable = !application.IsFramework,
            });
        }

        foreach (WinGetPackage package in packages)
        {
            if (matchedIds.Contains(package.PackageId))
            {
                continue;
            }

            updated.Add(new ScannedApplication(
                package.Name ?? package.PackageId,
                package.Version,
                null,
                null,
                package.PackageId,
                "WinGet",
                "Unknown",
                "Unknown",
                "WinGet",
                true));
        }

        applications.Clear();
        foreach (ScannedApplication application in updated)
        {
            applications.Add(application);
        }
    }

    private static List<ScannedApplication> Deduplicate(
        IEnumerable<ScannedApplication> applications)
    {
        Dictionary<string, ScannedApplication> unique = new(StringComparer.OrdinalIgnoreCase);
        foreach (ScannedApplication application in applications)
        {
            string identity = application.WinGetId is not null
                ? $"winget:{application.WinGetId}"
                : application.PackageFamilyName is not null
                    ? $"msix:{application.PackageFamilyName}"
                    : $"name:{Normalize(application.Name)}|{Normalize(application.Publisher)}|{Normalize(application.Version)}";
            unique.TryAdd(identity, application);
        }

        return unique.Values
            .OrderBy(application => application.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static bool IsMatch(ScannedApplication application, WinGetPackage package)
    {
        string applicationName = Normalize(application.Name);
        string packageName = Normalize(package.Name);
        string packageId = Normalize(package.PackageId);
        string packageIdTail = Normalize(package.PackageId.Split('.').Last());

        return applicationName.Length > 0 &&
            (applicationName == packageName ||
             applicationName == packageId ||
             applicationName == packageIdTail ||
             (application.PackageFamilyName is not null &&
              Normalize(application.PackageFamilyName).StartsWith(packageIdTail, StringComparison.Ordinal)));
    }

    private static string Normalize(string? value)
    {
        return string.Concat((value ?? string.Empty).Where(char.IsLetterOrDigit)).ToUpperInvariant();
    }
}
