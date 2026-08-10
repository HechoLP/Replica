# Portable Snapshot export

Replica can copy an existing `.replica` archive to a user-selected USB drive, external SSD/HDD, ordinary folder, network location, or cloud-synchronized folder. The source Snapshot remains unchanged and the destination is never silently overwritten.

## Destination inspection

Before export, Replica resolves the destination volume and reports:

- write access using a short-lived probe file;
- available free space;
- the reported file system;
- the FAT32 single-file limit of 4 GiB minus one byte;
- removable, fixed, network, or synchronized-folder classification;
- blocking reparse points and existing-name collisions.

OneDrive roots are discovered from Windows environment registration. Conventional Google Drive and Dropbox profile folders are recognized. Any other synchronized folder can be marked explicitly by the user. Synchronized destinations always show a warning to wait for synchronization and check for conflict copies.

Windows reports many USB-connected SSDs and HDDs as fixed drives, so Replica cannot reliably infer their physical connection from `DriveInfo`. Removable drives are marked external automatically; a fixed external SSD/HDD can be confirmed explicitly by the user for the checklist.

The file name must be a single safe leaf name ending in `.replica`. Device names, traversal, rooted paths, invalid characters, trailing dots/spaces, and overlong names are rejected.

## Atomic copy and verification

Replica first computes the source SHA-256. It writes a uniquely named `.partial` sibling with asynchronous, cancellable streaming I/O, flushes it, computes the copied file's SHA-256, and compares the size and hash. Only a verified copy is renamed to the final `.replica` name. Cancellation, hash mismatch, I/O failure, and a destination collision leave no final file and trigger best-effort partial-file cleanup.

The selected default Snapshot folder is stored as a small, atomic application setting under `%LOCALAPPDATA%\Replica`. Snapshot contents and paths are not uploaded to a cloud API; cloud providers see the file only through their normal local synchronization client.

## Before-reset checklist

After a verified export the WPF view shows:

- Snapshot saved and SHA-256 verified;
- whether the destination is outside the PC;
- whether an independent second copy exists;
- whether `ReplicaSetup.exe` is stored separately or must be downloaded later;
- an encryption-password reminder (Replica never stores or recovers the password);
- a real open-and-checksum validation test of the exported Snapshot;
- the accounts and licensed products the user expects to sign into manually after reset.

The checklist is guidance, not permission to reset Windows, and Replica never initiates a reset or reboot.

## Storing ReplicaSetup.exe

With explicit approval, Replica can download `ReplicaSetup.exe` beside the recovery material, not inside the Snapshot. The user selects either the latest stable release or an exact release tag.

The downloader accepts only a published release and an exact `ReplicaSetup.exe` asset from the HTTPS GitHub paths for `HechoLP/Replica`. Draft releases and foreign owners, repositories, hosts, asset names, redirects, or unsafe sizes are rejected. The destination receives a uniquely named `.download` file first. Replica enforces the declared asset size, computes SHA-256 while streaming, verifies the GitHub-provided SHA-256 digest when available, flushes the file, and only then renames it to `ReplicaSetup.exe`. Existing installers are not overwritten and the installer is never executed automatically.

Tests use fake volumes, write probes, hash services, and a mocked official release source. They never write to a real USB device, contact GitHub, run an installer, or alter Windows settings.
