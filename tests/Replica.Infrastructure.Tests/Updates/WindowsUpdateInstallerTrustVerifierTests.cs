using Replica.Core.Snapshots;
using Replica.Infrastructure.Security;
using Replica.Infrastructure.Updates;

namespace Replica.Infrastructure.Tests.Updates;

public sealed class WindowsUpdateInstallerTrustVerifierTests
{
    [Fact]
    public async Task VerifyAsync_AllowsOnlyTrustedPinnedPublisherCertificate()
    {
        string pinned = new('A', 64);
        WindowsUpdateInstallerTrustVerifier verifier = new(
            new FakeAuthenticodeTrustService(
                OfflineInstallerSignatureStatus.Trusted,
                pinned),
            [pinned]);

        bool result = await verifier.VerifyAsync("ReplicaSetup.exe", CancellationToken.None);

        Assert.True(verifier.IsTrustPolicyConfigured);
        Assert.True(result);
    }

    [Fact]
    public async Task VerifyAsync_RejectsTrustedButUnpinnedPublisherCertificate()
    {
        WindowsUpdateInstallerTrustVerifier verifier = new(
            new FakeAuthenticodeTrustService(
                OfflineInstallerSignatureStatus.Trusted,
                new string('B', 64)),
            [new string('A', 64)]);

        bool result = await verifier.VerifyAsync("ReplicaSetup.exe", CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public void InvalidPublisherPolicyRemainsFailClosed()
    {
        WindowsUpdateInstallerTrustVerifier verifier = new(
            new FakeAuthenticodeTrustService(
                OfflineInstallerSignatureStatus.Trusted,
                new string('A', 64)),
            ["not-a-certificate-hash"]);

        Assert.False(verifier.IsTrustPolicyConfigured);
    }

    private sealed class FakeAuthenticodeTrustService(
        OfflineInstallerSignatureStatus status,
        string certificateSha256) : IAuthenticodeTrustService
    {
        public Task<AuthenticodeInspection> InspectAsync(
            string filePath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new AuthenticodeInspection(
                status,
                "CN=Replica Publisher",
                certificateSha256,
                status.ToString()));
        }
    }
}
