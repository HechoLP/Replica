using System.Text.Json;
using Replica.Core.Recovery;
using Replica.Core.Scanning;
using Replica.Core.Services;

namespace Replica.Infrastructure.Recovery;

public sealed class WindowsRecoveryHardwareScanner : IRecoveryHardwareScanner
{
    private readonly IProcessRunner _processRunner;

    public WindowsRecoveryHardwareScanner(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public async Task<ReplicaHardwareInfo> ScanAsync(CancellationToken cancellationToken)
    {
        ProcessExecutionResult result = await _processRunner.RunAsync(
            new ProcessRequest(ProcessOperation.HardwareInventory, TimeSpan.FromSeconds(20)),
            cancellationToken).ConfigureAwait(false);
        HardwareJson? hardware = null;
        if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            try
            {
                hardware = JsonSerializer.Deserialize<HardwareJson>(
                    result.StandardOutput,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException)
            {
                hardware = null;
            }
        }

        string[] drives;
        try
        {
            drives = DriveInfo.GetDrives()
                .Where(drive => drive.IsReady)
                .Select(drive => drive.RootDirectory.FullName)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (IOException)
        {
            drives = [];
        }

        return new ReplicaHardwareInfo(
            hardware?.GraphicsAdapters?.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray() ?? [],
            hardware?.Displays?.Select(display => new ReplicaDisplayInfo(
                display.Name ?? "Display",
                Math.Max(0, display.Width),
                Math.Max(0, display.Height),
                display.Primary)).ToArray() ?? [],
            hardware?.AudioDevices?.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray() ?? [],
            drives);
    }

    private sealed record HardwareJson(
        string[]? GraphicsAdapters,
        DisplayJson[]? Displays,
        string[]? AudioDevices);

    private sealed record DisplayJson(
        string? Name,
        int Width,
        int Height,
        bool Primary);
}
