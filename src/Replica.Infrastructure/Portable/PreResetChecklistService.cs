using Replica.Core.Portable;
using Replica.Core.Services;

namespace Replica.Infrastructure.Portable;

public sealed class PreResetChecklistService : IPreResetChecklistService
{
    public PreResetChecklist Create(PreResetChecklistRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Export);
        ArgumentNullException.ThrowIfNull(request.RequiredAccounts);

        bool isExternal = request.Export.Storage.IsExternalStorage;
        string accounts = request.RequiredAccounts.Count == 0
            ? "No account reminders were added. Review Microsoft, game, creative-tool, VPN, browser, and SSH access."
            : string.Join(", ", request.RequiredAccounts.Distinct(StringComparer.OrdinalIgnoreCase));

        return new PreResetChecklist(
        [
            Item("snapshot", "Snapshot saved", request.Export.DestinationPath, true),
            Item("hash", "SHA-256 verified", request.Export.Sha256, true),
            Item(
                "external",
                "Stored outside the PC",
                isExternal
                    ? "The snapshot is on removable or network storage."
                    : "Confirm this folder survives Windows reset and finishes cloud synchronization.",
                isExternal,
                warningWhenFalse: true),
            Item(
                "copy",
                "Independent copy",
                request.HasIndependentCopy
                    ? "A second copy is available."
                    : "Keep another verified copy in a different location.",
                request.HasIndependentCopy,
                warningWhenFalse: true),
            Item(
                "installer",
                "ReplicaSetup.exe available",
                request.InstallerStoredSeparately
                    ? "The installer is stored separately from the snapshot."
                    : "Download ReplicaSetup.exe from the official GitHub Release after reset or store it separately now.",
                request.InstallerStoredSeparately,
                warningWhenFalse: true),
            Item(
                "password",
                "Encryption password remembered",
                request.IsEncrypted
                    ? "Replica cannot recover or store the encryption password."
                    : "This snapshot was marked as not encrypted.",
                !request.IsEncrypted,
                warningWhenFalse: true),
            Item(
                "open-test",
                "Snapshot open test",
                request.SnapshotOpenTested
                    ? "The exported snapshot opened and validated successfully."
                    : "Open the exported snapshot once before resetting Windows.",
                request.SnapshotOpenTested),
            Item("accounts", "Accounts needed after reset", accounts, request.RequiredAccounts.Count > 0, warningWhenFalse: true),
        ]);
    }

    private static PreResetChecklistItem Item(
        string id,
        string title,
        string guidance,
        bool complete,
        bool warningWhenFalse = false) => new(
            id,
            title,
            guidance,
            complete
                ? PreResetChecklistStatus.Complete
                : warningWhenFalse
                    ? PreResetChecklistStatus.Warning
                    : PreResetChecklistStatus.ActionRequired);
}
