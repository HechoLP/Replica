using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Replica.Core.Platforms;

namespace Replica.Mac.Infrastructure.Scanning;

public sealed class HomebrewInventorySource : IHomebrewInventorySource
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private const int MaximumOutputCharacters = 1024 * 1024;
    private readonly IReadOnlyList<string> candidates;

    public HomebrewInventorySource()
        : this(["/opt/homebrew/bin/brew", "/usr/local/bin/brew"])
    {
    }

    public HomebrewInventorySource(IReadOnlyList<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        this.candidates = candidates.Select(Path.GetFullPath).ToArray();
    }

    public async Task<HomebrewInventoryResult> ReadAsync(CancellationToken cancellationToken)
    {
        string? executable = candidates.FirstOrDefault(File.Exists);
        if (executable is null)
        {
            return new HomebrewInventoryResult([], []);
        }

        List<PlatformApplication> applications = [];
        List<PlatformScanWarning> warnings = [];
        foreach (HomebrewPackageKind kind in Enum.GetValues<HomebrewPackageKind>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            BrewResult result = await RunAsync(executable, kind, cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                warnings.Add(new PlatformScanWarning(
                    "Homebrew",
                    result.TimedOut ? "Timeout" : "InventoryFailed",
                    "Homebrew inventory could not be read completely."));
                continue;
            }

            foreach (string line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string[] parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 0 || !IsSafePackageId(parts[0]))
                {
                    continue;
                }

                string packageId = parts[0];
                string? version = parts.Length > 1 ? parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0] : null;
                applications.Add(new PlatformApplication(
                    packageId,
                    version,
                    "Homebrew",
                    null,
                    null,
                    packageId,
                    kind == HomebrewPackageKind.Cask ? "HomebrewCask" : "HomebrewFormula",
                    "Unknown",
                    IsRestorable: true));
            }
        }

        IReadOnlyList<PlatformApplication> distinct = applications
            .GroupBy(application => $"{application.Source}:{application.HomebrewPackageId}", StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(application => application.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new HomebrewInventoryResult(distinct, warnings);
    }

    private static async Task<BrewResult> RunAsync(
        string executable,
        HomebrewPackageKind kind,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("list");
        startInfo.ArgumentList.Add("--versions");
        startInfo.ArgumentList.Add(kind == HomebrewPackageKind.Cask ? "--cask" : "--formula");

        using Process process = new() { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return new BrewResult(false, false, string.Empty);
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return new BrewResult(false, false, string.Empty);
        }

        using CancellationTokenSource timeoutSource = new(Timeout);
        using CancellationTokenSource linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        Task<string> stdout = ReadBoundedAsync(process.StandardOutput, linkedSource.Token);
        Task<string> stderr = ReadBoundedAsync(process.StandardError, linkedSource.Token);
        try
        {
            await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
            string output = await stdout.ConfigureAwait(false);
            _ = await stderr.ConfigureAwait(false);
            return new BrewResult(process.ExitCode == 0, false, output);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return new BrewResult(false, true, string.Empty);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        char[] buffer = new char[4096];
        StringBuilder value = new(Math.Min(MaximumOutputCharacters, 16 * 1024));
        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            int remaining = MaximumOutputCharacters - value.Length;
            if (remaining > 0)
            {
                value.Append(buffer, 0, Math.Min(read, remaining));
            }
        }

        return value.ToString();
    }

    private static bool IsSafePackageId(string value)
    {
        return value.Length is > 0 and <= 200 && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '+' or '.' or '@');
    }

    private static void TryKill(Process process)
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
        }
    }

    private enum HomebrewPackageKind
    {
        Formula,
        Cask,
    }

    private sealed record BrewResult(bool Succeeded, bool TimedOut, string Output);
}
