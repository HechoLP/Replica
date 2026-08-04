using System.Diagnostics;
using System.Text;
using Replica.Core.Scanning;
using Replica.Core.Services;

namespace Replica.Infrastructure.Scanning;

public sealed class ProcessRunner : IProcessRunner
{
    private const string MsixInventoryScript =
        "Get-AppxPackage | Select-Object Name,PackageFullName,PackageFamilyName,Publisher,Version,Architecture,InstallLocation,IsFramework,SignatureKind,NonRemovable | ConvertTo-Json -Compress -Depth 3";

    private readonly IReplicaPathProvider _pathProvider;

    public ProcessRunner(IReplicaPathProvider pathProvider)
    {
        _pathProvider = pathProvider;
    }

    public bool IsToolAvailable(ProcessTool tool)
    {
        return ResolveExecutable(tool) is not null;
    }

    public async Task<ProcessExecutionResult> RunAsync(
        ProcessRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "A positive timeout is required.");
        }

        if (request.MaximumOutputCharacters is < 1 or > 4_194_304)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "The output character limit must be between 1 and 4,194,304.");
        }

        if (request.Operation == ProcessOperation.WinGetInstall &&
            !IsValidPackageIdentifier(request.PackageIdentifier))
        {
            throw new ArgumentException("A valid winget package identifier is required.", nameof(request));
        }

        cancellationToken.ThrowIfCancellationRequested();

        ProcessTool tool = request.Operation == ProcessOperation.MsixInventory
            ? ProcessTool.PowerShell
            : ProcessTool.WinGet;
        string? executable = ResolveExecutable(tool);
        if (executable is null)
        {
            return new ProcessExecutionResult(
                null,
                string.Empty,
                "The requested scanner tool is not available.",
                false,
                false);
        }

        string? exportPath = null;
        ProcessStartInfo startInfo = new(executable)
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        switch (request.Operation)
        {
            case ProcessOperation.WinGetExport:
                Directory.CreateDirectory(_pathProvider.TemporaryDirectory);
                exportPath = Path.Combine(
                    _pathProvider.TemporaryDirectory,
                    $"winget-export-{Guid.NewGuid():N}.json");
                startInfo.ArgumentList.Add("export");
                startInfo.ArgumentList.Add("--output");
                startInfo.ArgumentList.Add(exportPath);
                startInfo.ArgumentList.Add("--include-versions");
                startInfo.ArgumentList.Add("--disable-interactivity");
                break;
            case ProcessOperation.WinGetList:
                startInfo.ArgumentList.Add("list");
                startInfo.ArgumentList.Add("--disable-interactivity");
                break;
            case ProcessOperation.WinGetInstall:
                startInfo.ArgumentList.Add("install");
                startInfo.ArgumentList.Add("--id");
                startInfo.ArgumentList.Add(request.PackageIdentifier!);
                startInfo.ArgumentList.Add("--exact");
                startInfo.ArgumentList.Add("--silent");
                startInfo.ArgumentList.Add("--disable-interactivity");
                startInfo.ArgumentList.Add("--accept-package-agreements");
                startInfo.ArgumentList.Add("--accept-source-agreements");
                break;
            case ProcessOperation.MsixInventory:
                startInfo.ArgumentList.Add("-NoLogo");
                startInfo.ArgumentList.Add("-NoProfile");
                startInfo.ArgumentList.Add("-NonInteractive");
                startInfo.ArgumentList.Add("-Command");
                startInfo.ArgumentList.Add(MsixInventoryScript);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request), request.Operation, null);
        }

        try
        {
            return await RunProcessAsync(
                startInfo,
                request,
                exportPath,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new ProcessExecutionResult(
                null,
                string.Empty,
                "The requested scanner tool could not be started.",
                false,
                false);
        }
        finally
        {
            if (exportPath is not null)
            {
                TryDeleteFile(exportPath);
            }
        }
    }

    private static async Task<ProcessExecutionResult> RunProcessAsync(
        ProcessStartInfo startInfo,
        ProcessRequest request,
        string? exportPath,
        CancellationToken cancellationToken)
    {
        using Process process = new() { StartInfo = startInfo };
        if (!process.Start())
        {
            return new ProcessExecutionResult(
                null,
                string.Empty,
                "The requested scanner tool could not be started.",
                false,
                false);
        }

        Task<BoundedText> standardOutputTask = ReadBoundedAsync(
            process.StandardOutput,
            request.MaximumOutputCharacters);
        Task<BoundedText> standardErrorTask = ReadBoundedAsync(
            process.StandardError,
            request.MaximumOutputCharacters);

        bool timedOut = false;
        using CancellationTokenSource timeoutSource = new(request.Timeout);
        using CancellationTokenSource linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        try
        {
            await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            KillProcess(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillProcess(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        BoundedText standardOutput = await standardOutputTask.ConfigureAwait(false);
        BoundedText standardError = await standardErrorTask.ConfigureAwait(false);

        if (!timedOut && exportPath is not null && process.ExitCode == 0)
        {
            standardOutput = await ReadBoundedFileAsync(
                exportPath,
                request.MaximumOutputCharacters,
                cancellationToken).ConfigureAwait(false);
        }

        return new ProcessExecutionResult(
            timedOut ? null : process.ExitCode,
            standardOutput.Value,
            standardError.Value,
            timedOut,
            standardOutput.Truncated || standardError.Truncated);
    }

    private static async Task<BoundedText> ReadBoundedAsync(
        StreamReader reader,
        int maximumCharacters)
    {
        char[] buffer = new char[4096];
        StringBuilder output = new(Math.Min(maximumCharacters, 16_384));
        bool truncated = false;

        while (true)
        {
            int count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            int remaining = maximumCharacters - output.Length;
            if (remaining > 0)
            {
                output.Append(buffer, 0, Math.Min(remaining, count));
            }

            truncated |= count > remaining;
        }

        return new BoundedText(output.ToString(), truncated);
    }

    private static async Task<BoundedText> ReadBoundedFileAsync(
        string path,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        FileInfo file = new(path);
        if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            return new BoundedText(string.Empty, false);
        }

        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using StreamReader reader = new(stream, Encoding.UTF8, true, 4096, leaveOpen: false);

        char[] buffer = new char[4096];
        StringBuilder output = new(Math.Min(maximumCharacters, 16_384));
        bool truncated = false;
        while (true)
        {
            int count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            int remaining = maximumCharacters - output.Length;
            if (remaining > 0)
            {
                output.Append(buffer, 0, Math.Min(remaining, count));
            }

            truncated |= count > remaining;
        }

        return new BoundedText(output.ToString(), truncated);
    }

    private static string? ResolveExecutable(ProcessTool tool)
    {
        string windows = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows);
        string localAppData = System.Environment.GetFolderPath(
            System.Environment.SpecialFolder.LocalApplicationData);

        string path = tool switch
        {
            ProcessTool.WinGet => Path.Combine(
                localAppData,
                "Microsoft",
                "WindowsApps",
                "winget.exe"),
            ProcessTool.PowerShell => Path.Combine(
                windows,
                "System32",
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe"),
            ProcessTool.WindowsTerminal => Path.Combine(
                localAppData,
                "Microsoft",
                "WindowsApps",
                "wt.exe"),
            _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, null),
        };

        return File.Exists(path) ? path : null;
    }

    private static bool IsValidPackageIdentifier(string? value)
    {
        return value is { Length: > 1 and <= 255 } &&
            value.Contains('.', StringComparison.Ordinal) &&
            value.All(character =>
                char.IsLetterOrDigit(character) || character is '.' or '-' or '_');
    }

    private static void KillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the state check and the kill request.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A stale export contains only package metadata and is cleaned on a later temp sweep.
        }
        catch (UnauthorizedAccessException)
        {
            // Failure to clean a read-only temporary export must not hide the scan result.
        }
    }

    private sealed record BoundedText(string Value, bool Truncated);
}
