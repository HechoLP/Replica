using System.Diagnostics;
using Replica.Core.Execution;
using Replica.Infrastructure.Restore;

namespace Replica.Infrastructure.Tests.Restore;

public sealed class ElevatedProcessLauncherTests
{
    [Fact]
    public async Task LaunchAsync_IgnoresParentCancellationAfterElevatedChildStarts()
    {
        using CancellationTokenSource parentCancellation = new();
        CancellationToken observedSafetyToken = new(canceled: true);
        ElevatedProcessLauncher launcher = new(async (
            ProcessStartInfo _,
            CancellationToken safetyToken) =>
        {
            observedSafetyToken = safetyToken;
            parentCancellation.Cancel();
            await Task.Yield();
            return 0;
        });
        ElevatedPlanLaunchRequest request = new(
            "plan.json",
            new string('A', 64),
            Convert.ToBase64String(new byte[32]),
            Guid.NewGuid().ToString("N"));

        int exitCode = await launcher.LaunchAsync(request, parentCancellation.Token);

        Assert.Equal(0, exitCode);
        Assert.False(observedSafetyToken.CanBeCanceled);
        Assert.True(parentCancellation.IsCancellationRequested);
    }
}
