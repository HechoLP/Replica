using Replica.Core.Platforms;
using Replica.Core.Services;
using Replica.Core.Snapshots;

namespace Replica.Mac.Infrastructure.Scanning;

public sealed class MacEnvironmentScanner : IPlatformEnvironmentScanner
{
    private const int TotalStages = 6;
    private readonly IMacSystemInfoSource systemInfoSource;
    private readonly IMacApplicationSource applicationSource;
    private readonly IHomebrewInventorySource homebrewSource;
    private readonly IMacEnvironmentSource environmentSource;
    private readonly IMacFontSource fontSource;

    public MacEnvironmentScanner(
        IMacSystemInfoSource systemInfoSource,
        IMacApplicationSource applicationSource,
        IHomebrewInventorySource homebrewSource,
        IMacEnvironmentSource environmentSource,
        IMacFontSource fontSource)
    {
        this.systemInfoSource = systemInfoSource;
        this.applicationSource = applicationSource;
        this.homebrewSource = homebrewSource;
        this.environmentSource = environmentSource;
        this.fontSource = fontSource;
    }

    public async Task<PlatformScanResult> ScanAsync(
        IProgress<PlatformScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        List<PlatformScanWarning> warnings = [];
        progress?.Report(new PlatformScanProgress(PlatformScanStage.SystemInformation, 0, TotalStages, "macOS 정보를 읽는 중입니다."));
        (ReplicaPlatformInfo platform, string machineName) = await systemInfoSource
            .ReadAsync(cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<PlatformApplication> bundleApplications = await ReadWithPartialFailureAsync(
            () => applicationSource.ReadAsync(cancellationToken),
            "ApplicationBundles",
            warnings,
            cancellationToken).ConfigureAwait(false);
        progress?.Report(new PlatformScanProgress(PlatformScanStage.Applications, 2, TotalStages, "응용 프로그램을 확인했습니다."));

        HomebrewInventoryResult homebrew;
        try
        {
            homebrew = await homebrewSource.ReadAsync(cancellationToken).ConfigureAwait(false);
            warnings.AddRange(homebrew.Warnings);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warnings.Add(new PlatformScanWarning("Homebrew", "InventoryFailed", "Homebrew inventory could not be read."));
            homebrew = new HomebrewInventoryResult([], []);
        }

        progress?.Report(new PlatformScanProgress(PlatformScanStage.HomebrewPackages, 3, TotalStages, "Homebrew 패키지를 확인했습니다."));

        MacEnvironmentResult environment;
        try
        {
            environment = await environmentSource.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warnings.Add(new PlatformScanWarning("Environment", "ReadFailed", "Environment data could not be read."));
            environment = new MacEnvironmentResult([], []);
        }

        progress?.Report(new PlatformScanProgress(PlatformScanStage.EnvironmentVariables, 4, TotalStages, "환경변수를 확인했습니다."));
        progress?.Report(new PlatformScanProgress(PlatformScanStage.Path, 5, TotalStages, "PATH를 확인했습니다."));

        IReadOnlyList<PlatformFont> fonts = await ReadWithPartialFailureAsync(
            () => fontSource.ReadAsync(cancellationToken),
            "Fonts",
            warnings,
            cancellationToken).ConfigureAwait(false);
        progress?.Report(new PlatformScanProgress(PlatformScanStage.Fonts, 6, TotalStages, "글꼴을 확인했습니다."));

        IReadOnlyList<PlatformApplication> applications = bundleApplications
            .Concat(homebrew.Applications)
            .OrderBy(application => application.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        int sensitiveCount = environment.Variables.Count(variable => variable.IsSensitive);
        PlatformScanSummary summary = new(
            applications.Count,
            homebrew.Applications.Count,
            environment.Variables.Count,
            sensitiveCount,
            fonts.Count,
            warnings.Count);
        progress?.Report(new PlatformScanProgress(PlatformScanStage.Completed, TotalStages, TotalStages, "스캔이 완료되었습니다."));
        return new PlatformScanResult(
            platform,
            machineName,
            applications,
            environment.Variables,
            environment.PathEntries,
            fonts,
            warnings,
            summary);
    }

    private static async Task<IReadOnlyList<T>> ReadWithPartialFailureAsync<T>(
        Func<Task<IReadOnlyList<T>>> operation,
        string provider,
        ICollection<PlatformScanWarning> warnings,
        CancellationToken cancellationToken)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warnings.Add(new PlatformScanWarning(provider, "ReadFailed", $"{provider} data could not be read."));
            return [];
        }
    }
}
