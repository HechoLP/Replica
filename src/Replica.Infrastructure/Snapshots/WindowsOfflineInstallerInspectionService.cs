using System.Diagnostics;
using System.Security.Cryptography;
using Replica.Core.Services;
using Replica.Core.Snapshots;
using Replica.Infrastructure.Security;

namespace Replica.Infrastructure.Snapshots;

public sealed class WindowsOfflineInstallerInspectionService : IOfflineInstallerInspectionService
{
    private const long MaximumInstallerSize = 2L * 1024 * 1024 * 1024;
    private readonly IAuthenticodeTrustService authenticode;
    private readonly SnapshotSelectionPolicy selectionPolicy;

    public WindowsOfflineInstallerInspectionService(IAuthenticodeTrustService authenticode)
        : this(authenticode, new SnapshotSelectionPolicy())
    {
    }

    internal WindowsOfflineInstallerInspectionService(
        IAuthenticodeTrustService authenticode,
        SnapshotSelectionPolicy selectionPolicy)
    {
        this.authenticode = authenticode;
        this.selectionPolicy = selectionPolicy;
    }

    public async Task<OfflineInstallerInspection> InspectAsync(
        string installerPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installerPath);
        cancellationToken.ThrowIfCancellationRequested();
        string fullPath = Path.GetFullPath(installerPath);
        if (!File.Exists(fullPath))
        {
            throw new ReplicaSnapshotException("The selected offline installer does not exist.");
        }

        selectionPolicy.ValidateOfflineInstaller(fullPath);
        if (SnapshotPathValidator.ContainsReparsePoint(fullPath))
        {
            throw new ReplicaSnapshotException("An offline installer cannot be a reparse point.");
        }

        await using FileStream lockedFile = new(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (lockedFile.Length is <= 0 or > MaximumInstallerSize)
        {
            throw new ReplicaSnapshotException("The offline installer size is outside the supported limit.");
        }

        string sha256 = await ComputeSha256Async(lockedFile, cancellationToken).ConfigureAwait(false);
        AuthenticodeInspection signature = await authenticode
            .InspectAsync(fullPath, cancellationToken)
            .ConfigureAwait(false);
        if (lockedFile.Length != new FileInfo(fullPath).Length ||
            SnapshotPathValidator.ContainsReparsePoint(fullPath))
        {
            throw new ReplicaSnapshotException("The offline installer changed during inspection.");
        }

        FileVersionInfo version = FileVersionInfo.GetVersionInfo(fullPath);
        string displayName = FirstNonEmpty(
            version.ProductName,
            version.FileDescription,
            Path.GetFileNameWithoutExtension(fullPath));
        string? productVersion = FirstNonEmptyOrNull(version.ProductVersion, version.FileVersion);
        string architecture = DetectPortableExecutableArchitecture(lockedFile) ??
            DetectArchitectureFromName(Path.GetFileName(fullPath)) ??
            "Unknown";

        OfflineInstallerSignatureStatus status = signature.Status;
        string statusMessage = signature.StatusMessage;
        if (status == OfflineInstallerSignatureStatus.Trusted &&
            (string.IsNullOrWhiteSpace(signature.Publisher) ||
             string.IsNullOrWhiteSpace(signature.CertificateSha256)))
        {
            status = OfflineInstallerSignatureStatus.VerificationUnavailable;
            statusMessage = "Windows trusted the signature, but Replica could not bind the publisher certificate.";
        }

        return new OfflineInstallerInspection(
            fullPath,
            displayName,
            productVersion,
            architecture,
            lockedFile.Length,
            sha256,
            status,
            signature.Publisher,
            signature.CertificateSha256,
            statusMessage);
    }

    private static async Task<string> ComputeSha256Async(
        Stream source,
        CancellationToken cancellationToken)
    {
        source.Position = 0;
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[128 * 1024];
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string? DetectPortableExecutableArchitecture(Stream source)
    {
        if (!source.CanSeek || source.Length < 64)
        {
            return null;
        }

        Span<byte> header = stackalloc byte[64];
        source.Position = 0;
        if (source.Read(header) != header.Length || header[0] != 'M' || header[1] != 'Z')
        {
            return null;
        }

        int peOffset = BitConverter.ToInt32(header[60..64]);
        if (peOffset < 0 || peOffset > source.Length - 6)
        {
            return null;
        }

        Span<byte> peHeader = stackalloc byte[6];
        source.Position = peOffset;
        if (source.Read(peHeader) != peHeader.Length ||
            peHeader[0] != 'P' || peHeader[1] != 'E' || peHeader[2] != 0 || peHeader[3] != 0)
        {
            return null;
        }

        ushort machine = BitConverter.ToUInt16(peHeader[4..6]);
        return machine switch
        {
            0x014c => "x86",
            0x8664 => "x64",
            0x01c4 => "ARM",
            0xAA64 => "ARM64",
            0x0200 => "IA64",
            _ => null,
        };
    }

    private static string? DetectArchitectureFromName(string fileName)
    {
        string normalized = fileName.ToLowerInvariant();
        if (ContainsToken(normalized, "arm64"))
        {
            return "ARM64";
        }

        if (ContainsToken(normalized, "x64") || ContainsToken(normalized, "amd64"))
        {
            return "x64";
        }

        if (ContainsToken(normalized, "x86") || ContainsToken(normalized, "win32"))
        {
            return "x86";
        }

        return null;
    }

    private static bool ContainsToken(string value, string token)
    {
        int index = value.IndexOf(token, StringComparison.Ordinal);
        while (index >= 0)
        {
            bool leftBoundary = index == 0 || !char.IsLetterOrDigit(value[index - 1]);
            int end = index + token.Length;
            bool rightBoundary = end == value.Length || !char.IsLetterOrDigit(value[end]);
            if (leftBoundary && rightBoundary)
            {
                return true;
            }

            index = value.IndexOf(token, index + 1, StringComparison.Ordinal);
        }

        return false;
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.First(value => !string.IsNullOrWhiteSpace(value))!;

    private static string? FirstNonEmptyOrNull(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
