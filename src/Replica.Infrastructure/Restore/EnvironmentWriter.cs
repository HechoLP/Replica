using Replica.Core.Execution;
using Replica.Core.Services;

namespace Replica.Infrastructure.Restore;

public interface IEnvironmentVariableStore
{
    string? Get(string name, EnvironmentVariableScope scope);

    void Set(string name, string value, EnvironmentVariableScope scope);
}

public interface IEnvironmentChangeNotifier
{
    void NotifyEnvironmentChanged();
}

public sealed class EnvironmentWriter : IEnvironmentWriter
{
    private const int MaximumEnvironmentValueLength = 32_767;
    private const int PathLengthWarningThreshold = 2_048;
    private readonly IEnvironmentChangeNotifier _notifier;
    private readonly IEnvironmentVariableStore _store;

    public EnvironmentWriter(
        IEnvironmentVariableStore store,
        IEnvironmentChangeNotifier notifier)
    {
        _store = store;
        _notifier = notifier;
    }

    public Task<EnvironmentWriteResult> WriteAsync(
        EnvironmentWriteRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Scope == EnvironmentVariableScope.Machine && !request.IsElevated)
        {
            return Task.FromResult(new EnvironmentWriteResult(
                RestoreExecutionState.Failed,
                "MachineScopeRequiresElevation",
                false,
                false));
        }

        string name = request.Name.Trim();
        string value = request.Value!;
        string? current = _store.Get(name, request.Scope);
        string nextValue;
        bool changed;
        if (request.IsPathEntry)
        {
            if (!name.Equals("PATH", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("PATH entry requests must target PATH.", nameof(request));
            }

            string normalizedEntry = NormalizePathEntry(value);
            string[] entries = (current ?? string.Empty)
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (entries.Any(entry =>
                    NormalizePathEntry(entry).Equals(normalizedEntry, StringComparison.OrdinalIgnoreCase)))
            {
                return Task.FromResult(new EnvironmentWriteResult(
                    RestoreExecutionState.Skipped,
                    "PathEntryAlreadyPresent",
                    false,
                    (current?.Length ?? 0) > PathLengthWarningThreshold));
            }

            nextValue = entries.Length == 0
                ? value.Trim()
                : $"{string.Join(';', entries)};{value.Trim()}";
            changed = true;
        }
        else
        {
            nextValue = value;
            changed = !string.Equals(current, nextValue, StringComparison.Ordinal);
            if (!changed)
            {
                return Task.FromResult(new EnvironmentWriteResult(
                    RestoreExecutionState.Skipped,
                    "EnvironmentValueAlreadyMatches",
                    false,
                    nextValue.Length > PathLengthWarningThreshold));
            }
        }

        if (nextValue.Length > MaximumEnvironmentValueLength)
        {
            return Task.FromResult(new EnvironmentWriteResult(
                RestoreExecutionState.Failed,
                "EnvironmentValueTooLong",
                false,
                true));
        }

        cancellationToken.ThrowIfCancellationRequested();
        _store.Set(name, nextValue, request.Scope);
        _notifier.NotifyEnvironmentChanged();
        return Task.FromResult(new EnvironmentWriteResult(
            RestoreExecutionState.Succeeded,
            request.IsPathEntry ? "PathEntryMerged" : "EnvironmentValueSet",
            changed,
            nextValue.Length > PathLengthWarningThreshold));
    }

    private static void ValidateRequest(EnvironmentWriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Scope) ||
            string.IsNullOrWhiteSpace(request.Name) ||
            request.Name.Length > 255 ||
            request.Name.Contains('=') ||
            request.Name.Any(char.IsControl) ||
            request.Value is null ||
            request.Name.Trim().Equals("PATH", StringComparison.OrdinalIgnoreCase) != request.IsPathEntry ||
            IsSensitiveName(request.Name))
        {
            throw new ArgumentException("The environment write request is invalid.", nameof(request));
        }
    }

    private static string NormalizePathEntry(string value)
    {
        string trimmed = value.Trim().Trim('"');
        if (trimmed.Length == 0 ||
            trimmed.Contains(';') ||
            trimmed.Any(char.IsControl))
        {
            throw new ArgumentException("The PATH entry is invalid.", nameof(value));
        }

        string expanded = System.Environment.ExpandEnvironmentVariables(trimmed);
        if (!Path.IsPathFullyQualified(expanded))
        {
            throw new ArgumentException("PATH entries must resolve to fully qualified paths.", nameof(value));
        }

        return Path.GetFullPath(expanded)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsSensitiveName(string name)
    {
        string normalized = name.Replace('-', '_').ToUpperInvariant();
        return normalized.Contains("TOKEN", StringComparison.Ordinal) ||
            normalized.Contains("SECRET", StringComparison.Ordinal) ||
            normalized.Contains("PASSWORD", StringComparison.Ordinal) ||
            normalized.Contains("KEY", StringComparison.Ordinal) ||
            normalized.Contains("CREDENTIAL", StringComparison.Ordinal) ||
            normalized.Contains("CONNECTION_STRING", StringComparison.Ordinal);
    }
}

public sealed class WindowsEnvironmentVariableStore : IEnvironmentVariableStore
{
    public string? Get(string name, EnvironmentVariableScope scope)
    {
        return System.Environment.GetEnvironmentVariable(name, ToTarget(scope));
    }

    public void Set(string name, string value, EnvironmentVariableScope scope)
    {
        System.Environment.SetEnvironmentVariable(name, value, ToTarget(scope));
    }

    private static EnvironmentVariableTarget ToTarget(EnvironmentVariableScope scope)
    {
        return scope switch
        {
            EnvironmentVariableScope.User => EnvironmentVariableTarget.User,
            EnvironmentVariableScope.Machine => EnvironmentVariableTarget.Machine,
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null),
        };
    }
}

public sealed class WindowsEnvironmentChangeNotifier : IEnvironmentChangeNotifier
{
    private const uint AbortIfHung = 0x0002;
    private const uint SettingChange = 0x001A;
    private static readonly nint BroadcastWindow = new(0xffff);

    public void NotifyEnvironmentChanged()
    {
        _ = SendMessageTimeout(
            BroadcastWindow,
            SettingChange,
            0,
            "Environment",
            AbortIfHung,
            5_000,
            out _);
    }

    [System.Runtime.InteropServices.DllImport(
        "user32.dll",
        EntryPoint = "SendMessageTimeoutW",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern nint SendMessageTimeout(
        nint window,
        uint message,
        nuint wParam,
        string lParam,
        uint flags,
        uint timeout,
        out nuint result);
}
