using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Replica.Core.Snapshots;

namespace Replica.Infrastructure.Security;

public sealed record AuthenticodeInspection(
    OfflineInstallerSignatureStatus Status,
    string? Publisher,
    string? CertificateSha256,
    string StatusMessage)
{
    public bool IsTrusted => Status == OfflineInstallerSignatureStatus.Trusted;
}

public interface IAuthenticodeTrustService
{
    Task<AuthenticodeInspection> InspectAsync(
        string filePath,
        CancellationToken cancellationToken);
}

public sealed class WindowsAuthenticodeTrustService : IAuthenticodeTrustService
{
    public Task<AuthenticodeInspection> InspectAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        cancellationToken.ThrowIfCancellationRequested();
        string fullPath = Path.GetFullPath(filePath);
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(new AuthenticodeInspection(
                OfflineInstallerSignatureStatus.VerificationUnavailable,
                null,
                null,
                "Authenticode verification is available only on Windows."));
        }

        return Task.Run(() => InspectCore(fullPath, cancellationToken), cancellationToken);
    }

    private static AuthenticodeInspection InspectCore(
        string fullPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AuthenticodeTrustResult trustResult = VerifyEmbeddedSignature(fullPath);
        cancellationToken.ThrowIfCancellationRequested();

        X509Certificate2? signer = trustResult.Signer;
        try
        {
            string? publisher = signer?.Subject;
            string? certificateSha256 = signer?.GetCertHashString(HashAlgorithmName.SHA256);
            if (trustResult.Status == 0 && signer is not null && certificateSha256?.Length == 64)
            {
                return new AuthenticodeInspection(
                    OfflineInstallerSignatureStatus.Trusted,
                    publisher,
                    certificateSha256,
                    "Windows verified the Authenticode signature and certificate chain.");
            }

            if (IsUnsignedStatus(trustResult.Status))
            {
                return new AuthenticodeInspection(
                    OfflineInstallerSignatureStatus.Unsigned,
                    publisher,
                    certificateSha256,
                    "The file does not contain a verifiable Authenticode signature.");
            }

            return new AuthenticodeInspection(
                OfflineInstallerSignatureStatus.Untrusted,
                publisher,
                certificateSha256,
                trustResult.Status == 0
                    ? "Windows verified the signature, but Replica could not bind the verified signer certificate."
                    : $"Windows rejected the Authenticode trust chain (0x{trustResult.Status:X8}).");
        }
        finally
        {
            signer?.Dispose();
        }
    }

    private static bool IsUnsignedStatus(int status) =>
        status is TrustENoSignature or TrustEProviderUnknown or TrustESubjectFormUnknown;

    private static AuthenticodeTrustResult VerifyEmbeddedSignature(string filePath)
    {
        IntPtr filePathPointer = IntPtr.Zero;
        IntPtr fileInfoPointer = IntPtr.Zero;
        IntPtr trustDataPointer = IntPtr.Zero;
        Guid action = GenericVerifyV2;
        bool stateOpened = false;
        try
        {
            filePathPointer = Marshal.StringToCoTaskMemUni(filePath);
            WinTrustFileInfo fileInfo = new()
            {
                StructureSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = filePathPointer,
            };
            fileInfoPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);

            WinTrustData trustData = new()
            {
                StructureSize = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = WinTrustDataUiChoice.None,
                RevocationChecks = WinTrustDataRevocationChecks.WholeChain,
                UnionChoice = WinTrustDataChoice.File,
                FileInfoPointer = fileInfoPointer,
                StateAction = WinTrustDataStateAction.Verify,
                ProviderFlags = WinTrustDataProviderFlags.RevocationCheckChainExcludeRoot |
                    WinTrustDataProviderFlags.Safer,
            };
            trustDataPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustData>());
            Marshal.StructureToPtr(trustData, trustDataPointer, fDeleteOld: false);

            int result = WinVerifyTrust(IntPtr.Zero, ref action, trustDataPointer);
            trustData = Marshal.PtrToStructure<WinTrustData>(trustDataPointer);
            stateOpened = trustData.StateData != IntPtr.Zero;
            X509Certificate2? signer = result == 0 && stateOpened
                ? TryCopyVerifiedSigner(trustData.StateData)
                : null;
            return new AuthenticodeTrustResult(result, signer);
        }
        finally
        {
            if (stateOpened && trustDataPointer != IntPtr.Zero)
            {
                WinTrustData trustData = Marshal.PtrToStructure<WinTrustData>(trustDataPointer);
                trustData.StateAction = WinTrustDataStateAction.Close;
                Marshal.StructureToPtr(trustData, trustDataPointer, fDeleteOld: false);
                _ = WinVerifyTrust(IntPtr.Zero, ref action, trustDataPointer);
            }

            if (trustDataPointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(trustDataPointer);
            }

            if (fileInfoPointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(fileInfoPointer);
            }

            if (filePathPointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(filePathPointer);
            }
        }
    }

    private static X509Certificate2? TryCopyVerifiedSigner(IntPtr stateData)
    {
        try
        {
            IntPtr providerData = WTHelperProvDataFromStateData(stateData);
            if (providerData == IntPtr.Zero)
            {
                return null;
            }

            // Replica accepts only the primary signer selected by the Authenticode provider.
            // This intentionally fails closed for files whose identity is available only from
            // an unrelated or secondary signature.
            IntPtr providerSigner = WTHelperGetProvSignerFromChain(
                providerData,
                signerIndex: 0,
                isCounterSigner: false,
                counterSignerIndex: 0);
            if (providerSigner == IntPtr.Zero)
            {
                return null;
            }

            IntPtr providerCertificate = WTHelperGetProvCertFromChain(
                providerSigner,
                certificateIndex: 0);
            if (providerCertificate == IntPtr.Zero)
            {
                return null;
            }

            CryptProviderCertificateHeader certificateHeader =
                Marshal.PtrToStructure<CryptProviderCertificateHeader>(providerCertificate);
            if (certificateHeader.CertificateContext == IntPtr.Zero)
            {
                return null;
            }

            using X509Certificate2 verifiedSigner = new(certificateHeader.CertificateContext);
            return X509CertificateLoader.LoadCertificate(verifiedSigner.RawData);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private const int TrustENoSignature = unchecked((int)0x800B0100);
    private const int TrustEProviderUnknown = unchecked((int)0x800B0001);
    private const int TrustESubjectFormUnknown = unchecked((int)0x800B0003);
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WinVerifyTrust(
        IntPtr windowHandle,
        ref Guid actionId,
        IntPtr trustData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr stateData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(
        IntPtr providerData,
        uint signerIndex,
        [MarshalAs(UnmanagedType.Bool)] bool isCounterSigner,
        uint counterSignerIndex);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvCertFromChain(
        IntPtr providerSigner,
        uint certificateIndex);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructureSize;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint StructureSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public WinTrustDataUiChoice UiChoice;
        public WinTrustDataRevocationChecks RevocationChecks;
        public WinTrustDataChoice UnionChoice;
        public IntPtr FileInfoPointer;
        public WinTrustDataStateAction StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public WinTrustDataProviderFlags ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptProviderCertificateHeader
    {
        public uint StructureSize;
        public IntPtr CertificateContext;
    }

    private sealed record AuthenticodeTrustResult(int Status, X509Certificate2? Signer);

    private enum WinTrustDataUiChoice : uint
    {
        None = 2,
    }

    private enum WinTrustDataRevocationChecks : uint
    {
        WholeChain = 1,
    }

    private enum WinTrustDataChoice : uint
    {
        File = 1,
    }

    private enum WinTrustDataStateAction : uint
    {
        Verify = 1,
        Close = 2,
    }

    [Flags]
    private enum WinTrustDataProviderFlags : uint
    {
        RevocationCheckChainExcludeRoot = 0x00000080,
        Safer = 0x00000100,
    }
}
