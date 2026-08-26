using Replica.Core.Snapshots;
using Replica.Infrastructure.Security;

namespace Replica.Infrastructure.Tests.Security;

public sealed class WindowsAuthenticodeTrustServiceTests
{
    [Fact]
    public async Task InspectAsync_ClassifiesUnsignedManagedAssemblyWithoutPublisherIdentity()
    {
        WindowsAuthenticodeTrustService service = new();
        string unsignedAssembly = typeof(WindowsAuthenticodeTrustServiceTests).Assembly.Location;

        AuthenticodeInspection result = await service.InspectAsync(
            unsignedAssembly,
            CancellationToken.None);

        Assert.Equal(OfflineInstallerSignatureStatus.Unsigned, result.Status);
        Assert.Null(result.Publisher);
        Assert.Null(result.CertificateSha256);
    }

    [Fact]
    public async Task InspectAsync_HonorsCancellationBeforeNativeVerification()
    {
        WindowsAuthenticodeTrustService service = new();
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.InspectAsync(
            typeof(WindowsAuthenticodeTrustServiceTests).Assembly.Location,
            cancellation.Token));
    }

    [Fact]
    public async Task InspectAsync_RejectsBlankPath()
    {
        WindowsAuthenticodeTrustService service = new();

        await Assert.ThrowsAsync<ArgumentException>(() => service.InspectAsync(
            " ",
            CancellationToken.None));
    }
}
