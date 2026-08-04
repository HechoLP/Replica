using Microsoft.Win32;
using Replica.Core.Execution;
using Replica.Core.Services;

namespace Replica.Infrastructure.Restore;

public interface IRegistryWriteAllowList
{
    bool IsAllowed(RegistryWriteRequest request);
}

public interface IRegistryValueStore
{
    object? GetValue(RegistryWriteRequest request);

    void SetValue(RegistryWriteRequest request, object value);

    void DeleteValue(RegistryWriteRequest request);
}

public sealed class RegistryWriter : IRegistryWriter
{
    private readonly IRegistryWriteAllowList _allowList;
    private readonly IRegistryValueStore _store;

    public RegistryWriter(IRegistryWriteAllowList allowList, IRegistryValueStore store)
    {
        _allowList = allowList;
        _store = store;
    }

    public Task<RegistryWriteResult> WriteAsync(
        RegistryWriteRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_allowList.IsAllowed(request))
        {
            return Task.FromResult(new RegistryWriteResult(
                RestoreExecutionState.Failed,
                "RegistryPathNotAllowListed",
                false));
        }

        if (request.Hive.Equals("HKLM", StringComparison.OrdinalIgnoreCase) && !request.IsElevated)
        {
            return Task.FromResult(new RegistryWriteResult(
                RestoreExecutionState.Failed,
                "MachineRegistryRequiresElevation",
                false));
        }

        object value = ConvertValue(request);
        object? current = _store.GetValue(request);
        if (ValuesEqual(current, value))
        {
            return Task.FromResult(new RegistryWriteResult(
                RestoreExecutionState.Skipped,
                "RegistryValueAlreadyMatches",
                false));
        }

        cancellationToken.ThrowIfCancellationRequested();
        _store.SetValue(request, value);
        return Task.FromResult(new RegistryWriteResult(
            RestoreExecutionState.Succeeded,
            "RegistryValueRestored",
            true));
    }

    private static void ValidateRequest(RegistryWriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Kind) ||
            request.Hive is not ("HKCU" or "HKLM") ||
            string.IsNullOrWhiteSpace(request.KeyPath) ||
            request.KeyPath.Length > 512 ||
            request.KeyPath.StartsWith('\\') ||
            request.KeyPath.EndsWith('\\') ||
            request.KeyPath.Contains("..", StringComparison.Ordinal) ||
            request.KeyPath.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(request.ValueName) ||
            request.ValueName.Length > 255 ||
            request.ValueName.Any(char.IsControl) ||
            request.Value is null)
        {
            throw new ArgumentException("The registry write request is invalid.", nameof(request));
        }
    }

    private static object ConvertValue(RegistryWriteRequest request)
    {
        return request.Kind switch
        {
            RegistryValueDataKind.String or RegistryValueDataKind.ExpandString => request.Value!,
            RegistryValueDataKind.DWord when int.TryParse(request.Value, out int value) => value,
            RegistryValueDataKind.QWord when long.TryParse(request.Value, out long value) => value,
            RegistryValueDataKind.MultiString => request.Value!.Split(
                '\n',
                StringSplitOptions.RemoveEmptyEntries),
            RegistryValueDataKind.Binary => Convert.FromBase64String(request.Value!),
            _ => throw new ArgumentException("The registry value cannot be converted.", nameof(request)),
        };
    }

    private static bool ValuesEqual(object? first, object second)
    {
        return first switch
        {
            byte[] bytes when second is byte[] other => bytes.SequenceEqual(other),
            string[] strings when second is string[] other => strings.SequenceEqual(other, StringComparer.Ordinal),
            _ => Equals(first, second),
        };
    }
}

public sealed class BuiltInRegistryWriteAllowList : IRegistryWriteAllowList
{
    private const string ReplicaSettingsPath = "Software\\Replica\\RestorableSettings";

    public bool IsAllowed(RegistryWriteRequest request)
    {
        return request.Hive.Equals("HKCU", StringComparison.OrdinalIgnoreCase) &&
            (request.KeyPath.Equals(ReplicaSettingsPath, StringComparison.OrdinalIgnoreCase) ||
             request.KeyPath.StartsWith($"{ReplicaSettingsPath}\\", StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class WindowsRegistryValueStore : IRegistryValueStore
{
    public object? GetValue(RegistryWriteRequest request)
    {
        using RegistryKey baseKey = OpenBaseKey(request.Hive);
        using RegistryKey? key = baseKey.OpenSubKey(request.KeyPath, writable: false);
        return key?.GetValue(request.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }

    public void SetValue(RegistryWriteRequest request, object value)
    {
        using RegistryKey baseKey = OpenBaseKey(request.Hive);
        using RegistryKey key = baseKey.CreateSubKey(request.KeyPath, writable: true);
        key.SetValue(request.ValueName, value, ToRegistryValueKind(request.Kind));
        key.Flush();
    }

    public void DeleteValue(RegistryWriteRequest request)
    {
        using RegistryKey baseKey = OpenBaseKey(request.Hive);
        using RegistryKey? key = baseKey.OpenSubKey(request.KeyPath, writable: true);
        key?.DeleteValue(request.ValueName, throwOnMissingValue: false);
        key?.Flush();
    }

    private static RegistryKey OpenBaseKey(string hive)
    {
        return RegistryKey.OpenBaseKey(
            hive.Equals("HKCU", StringComparison.OrdinalIgnoreCase)
                ? RegistryHive.CurrentUser
                : RegistryHive.LocalMachine,
            RegistryView.Default);
    }

    private static RegistryValueKind ToRegistryValueKind(RegistryValueDataKind kind)
    {
        return kind switch
        {
            RegistryValueDataKind.String => RegistryValueKind.String,
            RegistryValueDataKind.ExpandString => RegistryValueKind.ExpandString,
            RegistryValueDataKind.DWord => RegistryValueKind.DWord,
            RegistryValueDataKind.QWord => RegistryValueKind.QWord,
            RegistryValueDataKind.MultiString => RegistryValueKind.MultiString,
            RegistryValueDataKind.Binary => RegistryValueKind.Binary,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }
}
