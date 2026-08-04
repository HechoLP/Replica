# Product specification

## Summary

Replica helps one person reconstruct a familiar Windows 11 setup after a clean installation. It records a portable description of the setup, compares that description with the current computer, proposes supported recovery actions, and executes only approved actions with verification and rollback evidence.

Replica does not copy Windows itself and does not promise byte-for-byte machine cloning. Its unit of recovery is a known application, setting, environment entry, font, development tool, or explicitly selected file.

## Target user and primary journey

The initial target is a technically comfortable Windows user preparing for a clean install or moving to a replacement PC.

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

Replica will create, read, validate, optionally encrypt, compare, and restore supported `.replica` snapshots. It will show confidence, risk, privilege, restart, rollback, manual-action, and compatibility information before execution.

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
- A valid snapshot can be moved between supported Windows 11 machines and reliably validated before use.
- Partial scans and restores preserve useful results and clearly report failures.
- The default workflow excludes sensitive information and requires explicit selection for user files.
- CI produces one tested, checksummed installer artifact for a tagged release.

## Constraints and open questions

- Initial runtime and UI architecture are Windows-only and `win-x64` self-contained.
- Code signing is desirable but not assumed. Unsigned builds must say so clearly.
- The repository owner must select a license before accepting external reuse or contributions.
- Supported application settings need an allow-listed plugin model; generic registry or AppData copying is outside the safe default.
