using Replica.Core.Plugins;
using Replica.Core.Scanning;
using Replica.Core.Services;

namespace Replica.Infrastructure.Scanning;

public sealed class EnvironmentScanner : IEnvironmentScanner
{
    private const int TotalStages = 7;
    private readonly IApplicationScanner _applicationScanner;
    private readonly IReadOnlyList<IBuiltInPlugin> _builtInPlugins;
    private readonly IEnvironmentVariableScanner _environmentVariableScanner;
    private readonly IFontScanner _fontScanner;
    private readonly IWindowsInfoScanner _windowsInfoScanner;

    public EnvironmentScanner(
        IWindowsInfoScanner windowsInfoScanner,
        IApplicationScanner applicationScanner,
        IEnvironmentVariableScanner environmentVariableScanner,
        IFontScanner fontScanner,
        IEnumerable<IBuiltInPlugin> builtInPlugins)
    {
        _windowsInfoScanner = windowsInfoScanner;
        _applicationScanner = applicationScanner;
        _environmentVariableScanner = environmentVariableScanner;
        _fontScanner = fontScanner;
        _builtInPlugins = builtInPlugins.ToArray();
    }

    public async Task<EnvironmentScanResult> ScanAsync(
        IProgress<EnvironmentScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        List<ScanWarning> warnings = [];
        WindowsEnvironmentInfo? windows = null;
        ApplicationScanResult applications = new([], 0, []);
        EnvironmentVariableScanResult environment = new([], [], 0, []);
        FontScanResult fonts = new([], []);

        progress?.Report(new EnvironmentScanProgress(
            EnvironmentScanStage.WindowsInformation,
            0,
            TotalStages,
            "Windows 정보를 읽는 중입니다."));
        try
        {
            windows = await _windowsInfoScanner.ScanAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            warnings.Add(new ScanWarning(
                "Windows",
                "WindowsInfoFailed",
                "Windows information could not be fully read."));
        }

        try
        {
            applications = await _applicationScanner
                .ScanAsync(progress, cancellationToken)
                .ConfigureAwait(false);
            warnings.AddRange(applications.Warnings);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            warnings.Add(new ScanWarning(
                "Applications",
                "ApplicationScanFailed",
                "Application inventory could not be fully read."));
        }

        progress?.Report(new EnvironmentScanProgress(
            EnvironmentScanStage.EnvironmentVariables,
            3,
            TotalStages,
            "환경변수를 읽는 중입니다."));
        try
        {
            environment = await _environmentVariableScanner
                .ScanAsync(cancellationToken)
                .ConfigureAwait(false);
            warnings.AddRange(environment.Warnings);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            warnings.Add(new ScanWarning(
                "Environment",
                "EnvironmentScanFailed",
                "Environment variables could not be fully read."));
        }

        progress?.Report(new EnvironmentScanProgress(
            EnvironmentScanStage.Path,
            4,
            TotalStages,
            "PATH 항목을 확인하는 중입니다."));
        cancellationToken.ThrowIfCancellationRequested();

        progress?.Report(new EnvironmentScanProgress(
            EnvironmentScanStage.Fonts,
            5,
            TotalStages,
            "글꼴 정보를 읽는 중입니다."));
        try
        {
            fonts = await _fontScanner.ScanAsync(cancellationToken).ConfigureAwait(false);
            warnings.AddRange(fonts.Warnings);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            warnings.Add(new ScanWarning(
                "Fonts",
                "FontScanFailed",
                "Installed font metadata could not be fully read."));
        }

        progress?.Report(new EnvironmentScanProgress(
            EnvironmentScanStage.BuiltInPlugins,
            6,
            TotalStages,
            "Built-in Plugin 정보를 확인하는 중입니다."));
        string[] pluginIds = _builtInPlugins
            .Select(plugin => plugin.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        EnvironmentScanSummary summary = new(
            applications.Applications.Count,
            applications.WinGetMatchCount,
            applications.Applications.Count - applications.WinGetMatchCount,
            environment.Variables.Count,
            environment.SensitiveExclusionCount,
            warnings.Count);

        progress?.Report(new EnvironmentScanProgress(
            EnvironmentScanStage.Completed,
            TotalStages,
            TotalStages,
            "스캔이 완료되었습니다."));
        return new EnvironmentScanResult(
            windows,
            applications.Applications,
            environment.Variables,
            environment.PathEntries,
            fonts.Fonts,
            pluginIds,
            warnings,
            summary);
    }
}
