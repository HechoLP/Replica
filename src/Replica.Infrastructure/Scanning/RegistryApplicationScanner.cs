using System.Runtime.InteropServices;
using Microsoft.Win32;
using Replica.Core.Scanning;
using Replica.Core.Services;

namespace Replica.Infrastructure.Scanning;

public interface IRegistryApplicationSource
{
    Task<IReadOnlyList<RegistryApplicationRecord>> ReadAsync(
        CancellationToken cancellationToken);
}

public sealed record RegistryApplicationRecord(
    string RegistryKeyName,
    string Name,
    string? Version,
    string? Publisher,
    string? InstallLocation,
    string Scope,
    string Architecture,
    bool IsWindowsInstaller);

public sealed class WindowsRegistryApplicationSource : IRegistryApplicationSource
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public Task<IReadOnlyList<RegistryApplicationRecord>> ReadAsync(
        CancellationToken cancellationToken)
    {
        return Task.Run<IReadOnlyList<RegistryApplicationRecord>>(
            () => ReadCore(cancellationToken),
            cancellationToken);
    }

    private static IReadOnlyList<RegistryApplicationRecord> ReadCore(
        CancellationToken cancellationToken)
    {
        List<RegistryApplicationRecord> applications = [];
        ReadHive(
            RegistryHive.LocalMachine,
            RegistryView.Registry64,
            "Machine",
            "x64",
            applications,
            cancellationToken);
        ReadHive(
            RegistryHive.LocalMachine,
            RegistryView.Registry32,
            "Machine",
            "x86",
            applications,
            cancellationToken);
        ReadHive(
            RegistryHive.CurrentUser,
            RegistryView.Default,
            "User",
            RuntimeInformation.ProcessArchitecture.ToString(),
            applications,
            cancellationToken);
        return applications;
    }

    private static void ReadHive(
        RegistryHive hive,
        RegistryView view,
        string scope,
        string architecture,
        ICollection<RegistryApplicationRecord> applications,
        CancellationToken cancellationToken)
    {
        using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
        using RegistryKey? uninstall = baseKey.OpenSubKey(UninstallPath, writable: false);
        if (uninstall is null)
        {
            return;
        }

        foreach (string keyName in uninstall.GetSubKeyNames())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using RegistryKey? entry = uninstall.OpenSubKey(keyName, writable: false);
                string? displayName = ReadString(entry, "DisplayName");
                if (displayName is null || IsEnabled(entry, "SystemComponent"))
                {
                    continue;
                }

                applications.Add(new RegistryApplicationRecord(
                    keyName,
                    displayName,
                    ReadString(entry, "DisplayVersion"),
                    ReadString(entry, "Publisher"),
                    ReadString(entry, "InstallLocation"),
                    scope,
                    architecture,
                    IsEnabled(entry, "WindowsInstaller")));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Registry inventory is untrusted. Skip only the unreadable entry.
            }
        }
    }

    private static string? ReadString(RegistryKey? key, string name)
    {
        return key?.GetValue(name) is string value && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;
    }

    private static bool IsEnabled(RegistryKey? key, string name)
    {
        object? value = key?.GetValue(name);
        return value switch
        {
            int number => number == 1,
            long number => number == 1,
            string text => int.TryParse(text, out int number) && number == 1,
            _ => false,
        };
    }
}

public sealed class RegistryApplicationScanner : IRegistryApplicationScanner
{
    private readonly IRegistryApplicationSource _source;

    public RegistryApplicationScanner(IRegistryApplicationSource source)
    {
        _source = source;
    }

    public async Task<RegistryApplicationScanResult> ScanAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<RegistryApplicationRecord> records = await _source
            .ReadAsync(cancellationToken)
            .ConfigureAwait(false);

        Dictionary<string, RegistryApplicationRecord> unique = new(
            StringComparer.OrdinalIgnoreCase);
        foreach (RegistryApplicationRecord record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string identity = CreateIdentity(record);
            if (unique.TryGetValue(identity, out RegistryApplicationRecord? existing))
            {
                unique[identity] = PreferComplete(existing, record);
            }
            else
            {
                unique.Add(identity, record);
            }
        }

        ScannedApplication[] applications = unique.Values
            .Select(record => new ScannedApplication(
                record.Name,
                record.Version,
                record.Publisher,
                record.InstallLocation,
                null,
                "Registry",
                record.Scope,
                record.Architecture,
                record.IsWindowsInstaller ? "MSI" : "Desktop",
                false,
                MsiProductCode: GetMsiProductCode(record)))
            .OrderBy(application => application.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        return new RegistryApplicationScanResult(applications, []);
    }

    private static string? GetMsiProductCode(RegistryApplicationRecord record)
    {
        return record.IsWindowsInstaller &&
            Guid.TryParse(record.RegistryKeyName.Trim('{', '}'), out Guid productCode)
            ? productCode.ToString("D")
            : null;
    }

    private static string CreateIdentity(RegistryApplicationRecord record)
    {
        if (Guid.TryParse(record.RegistryKeyName.Trim('{', '}'), out Guid productCode))
        {
            return $"msi:{productCode:D}";
        }

        return string.Join(
            '|',
            NormalizeIdentity(record.Name),
            NormalizeIdentity(record.Publisher),
            NormalizeIdentity(record.Version));
    }

    private static RegistryApplicationRecord PreferComplete(
        RegistryApplicationRecord first,
        RegistryApplicationRecord second)
    {
        int firstScore = Score(first);
        int secondScore = Score(second);
        if (secondScore <= firstScore)
        {
            return first;
        }

        return second;
    }

    private static int Score(RegistryApplicationRecord record)
    {
        return (record.Version is null ? 0 : 1) +
            (record.Publisher is null ? 0 : 1) +
            (record.InstallLocation is null ? 0 : 1) +
            (record.Architecture.Equals("x64", StringComparison.OrdinalIgnoreCase) ? 1 : 0);
    }

    private static string NormalizeIdentity(string? value)
    {
        return string.Concat(
            (value ?? string.Empty)
                .Where(char.IsLetterOrDigit))
            .ToUpperInvariant();
    }
}
