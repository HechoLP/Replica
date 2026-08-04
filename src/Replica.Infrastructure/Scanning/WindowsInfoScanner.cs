using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using Replica.Core.Scanning;
using Replica.Core.Services;

namespace Replica.Infrastructure.Scanning;

public sealed class WindowsInfoScanner : IWindowsInfoScanner
{
    private const string WindowsVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private readonly IProcessRunner _processRunner;

    public WindowsInfoScanner(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public Task<WindowsEnvironmentInfo> ScanAsync(CancellationToken cancellationToken)
    {
        return Task.Run(
            () => ScanCore(cancellationToken),
            cancellationToken);
    }

    private WindowsEnvironmentInfo ScanCore(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using RegistryKey baseKey = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine,
            RegistryView.Registry64);
        using RegistryKey? versionKey = baseKey.OpenSubKey(WindowsVersionKey, writable: false);

        string edition = ReadString(versionKey, "ProductName") ??
            ReadString(versionKey, "EditionID") ??
            "Unknown";
        string version = ReadString(versionKey, "DisplayVersion") ??
            ReadString(versionKey, "ReleaseId") ??
            System.Environment.OSVersion.Version.ToString();
        string build = ReadString(versionKey, "CurrentBuildNumber") ??
            System.Environment.OSVersion.Version.Build.ToString(CultureInfo.InvariantCulture);
        if (versionKey?.GetValue("UBR") is int updateBuildRevision)
        {
            build = $"{build}.{updateBuildRevision.ToString(CultureInfo.InvariantCulture)}";
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new WindowsEnvironmentInfo(
            edition,
            version,
            build,
            RuntimeInformation.OSArchitecture.ToString(),
            CultureInfo.CurrentUICulture.Name,
            TimeZoneInfo.Local.Id,
            System.Environment.MachineName,
            IsAdministrator(),
            _processRunner.IsToolAvailable(ProcessTool.WinGet),
            _processRunner.IsToolAvailable(ProcessTool.PowerShell),
            _processRunner.IsToolAvailable(ProcessTool.WindowsTerminal));
    }

    private static string? ReadString(RegistryKey? key, string name)
    {
        return key?.GetValue(name) is string value && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;
    }

    private static bool IsAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        WindowsPrincipal principal = new(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}
