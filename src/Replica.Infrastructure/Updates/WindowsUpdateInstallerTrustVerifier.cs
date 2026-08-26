using System.Reflection;
using Replica.Infrastructure.Security;

namespace Replica.Infrastructure.Updates;

public interface IUpdateInstallerTrustVerifier
{
    bool IsTrustPolicyConfigured { get; }

    Task<bool> VerifyAsync(string installerPath, CancellationToken cancellationToken);
}

public sealed class WindowsUpdateInstallerTrustVerifier : IUpdateInstallerTrustVerifier
{
    private readonly IAuthenticodeTrustService authenticode;
    private readonly IReadOnlySet<string> trustedCertificateSha256;

    public WindowsUpdateInstallerTrustVerifier(IAuthenticodeTrustService authenticode)
        : this(authenticode, LoadConfiguredCertificateHashes())
    {
    }

    internal WindowsUpdateInstallerTrustVerifier(
        IAuthenticodeTrustService authenticode,
        IEnumerable<string> trustedCertificateSha256)
    {
        this.authenticode = authenticode;
        this.trustedCertificateSha256 = trustedCertificateSha256
            .Select(NormalizeCertificateHash)
            .Where(hash => hash is not null)
            .Select(hash => hash!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public bool IsTrustPolicyConfigured => trustedCertificateSha256.Count > 0;

    public async Task<bool> VerifyAsync(string installerPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installerPath);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsTrustPolicyConfigured)
        {
            return false;
        }

        AuthenticodeInspection inspection = await authenticode
            .InspectAsync(installerPath, cancellationToken)
            .ConfigureAwait(false);
        return inspection.IsTrusted &&
            inspection.CertificateSha256 is not null &&
            trustedCertificateSha256.Contains(inspection.CertificateSha256);
    }

    private static IEnumerable<string> LoadConfiguredCertificateHashes()
    {
        return typeof(WindowsUpdateInstallerTrustVerifier).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(attribute => attribute.Key.Equals(
                "ReplicaPublisherCertificateSha256",
                StringComparison.Ordinal))
            .SelectMany(attribute => (attribute.Value ?? string.Empty).Split(
                [',', ';'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string? NormalizeCertificateHash(string value)
    {
        string normalized = value.Replace(":", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            return null;
        }

        return normalized;
    }
}
