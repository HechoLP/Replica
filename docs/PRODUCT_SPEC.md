# Product specification

## Summary

Replica helps one person record a familiar desktop setup and, on Windows 11, reconstruct supported differences after a clean installation. Windows compares the portable description with the current computer, proposes supported recovery actions, and executes only approved actions with verification and rollback evidence. The macOS 13+ Preview is deliberately read-only and creates validated Lightweight Snapshots without promising restore parity.

Replica does not copy Windows itself and does not promise byte-for-byte machine cloning. Its unit of recovery is a known application, setting, environment entry, font, development tool, or explicitly selected file.

## Target user and primary journey

The full recovery target is a technically comfortable Windows user preparing for a clean install or moving to a replacement PC. A Mac user can scan application bundles, Homebrew inventory, non-sensitive environment/PATH data, and font metadata, then create or inspect a Lightweight Snapshot without changing the Mac.

1. Scan the current PC.
2. Review exclusions and create a Recovery Snapshot.
3. Store the `.replica` file on user-chosen removable or cloud-synchronized storage.
4. Clean-install Windows.
5. Download and install `ReplicaSetup.exe` from GitHub Releases.
6. Open the snapshot and scan the new PC.
7. Review differences and a dry-run restore plan.
8. Select actions, approve elevation only where required, and restore.
9. Verify results and roll back Replica-owned changes if necessary.

Replica also supports opening a snapshot on an existing PC, comparing it, choosing only selected differences, restoring, and verifying the result.

## Functional scope

Replica will inventory:

- desktop applications, versions, publishers, architectures, install scope, and known package identities;
- winget packages and Microsoft Store/MSIX applications;
- user and machine environment variables, with PATH represented as ordered entries;
- Windows edition, version, build, architecture, locale, time zone, and tool availability;
- development tools and supported plugin-provided configuration;
- installed font metadata;
- selected application/game settings and explicitly selected user files.

Replica will create, read, validate, optionally encrypt, compare, and restore supported `.replica` snapshots. Windows Offline Recovery Packs may contain only explicitly selected, Windows-trusted installers bound to reviewed publisher-certificate, hash, size, provenance, architecture, and licensing metadata; recovery exports but never auto-executes them. Replica will show confidence, risk, privilege, restart, rollback, manual-action, and compatibility information before execution.

The macOS Preview creates and validates unencrypted Lightweight Snapshots and can inspect unencrypted Snapshot metadata. Recovery payload capture, encryption-password UI, comparison-to-restore, execution, elevation, restart/resume, and rollback remain Windows-only until typed macOS policies and journals exist.

## Restore modes

- **Safe** installs missing supported applications only. It preserves extras, blocks downgrades/removals, and asks about conflicts. This is the default.
- **Recommended** also restores compatible settings, merges environment data, and proposes safe updates.
- **Exact** exposes the fullest achievable comparison. Removals, downgrades, and other high-risk differences remain manual and individually approved; Replica does not treat Exact as permission to damage the target system.

## Non-goals

- disk imaging, Windows deployment, or full user-profile migration;
- password, browser session, credential, token, private-key, payment, or recovery-key migration;
- unattended destructive convergence;
- automatic removal of extra software or automatic software downgrades;
- bypassing software licensing, DRM, or installer terms;
- backing up all personal files by default;
- claiming recoverability for unsupported applications.

## Success criteria

- A first-time user can understand the three snapshot types and Safe/Recommended/Exact modes without documentation.
- Every proposed machine mutation is visible in a dry run and carries risk, elevation, restart, rollback, dependency, and support metadata.
- A valid snapshot can be moved between supported clients and reliably validated before use; restoration is permitted only on a platform with explicit typed support.
- Partial scans and restores preserve useful results and clearly report failures.
- The default workflow excludes sensitive information and requires explicit selection for user files.
- CI produces tested, checksummed Windows x64, macOS ARM64, and macOS x64 installer artifacts for a tagged release, and blocks Stable publication unless Windows signing and Apple notarization succeed.

## Constraints and open questions

- Windows is `win-x64` self-contained; macOS Preview packages are `osx-arm64` and `osx-x64` self-contained Avalonia app bundles.
- Unsigned builds must say so clearly. Update lookup and download may remain available, but in-app installer launch requires a configured trusted Authenticode publisher policy and otherwise fails closed.
- The repository owner must select a license before accepting external reuse or contributions.
- Supported application settings need an allow-listed plugin model; generic registry or AppData copying is outside the safe default.
