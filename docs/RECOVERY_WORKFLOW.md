# Recovery workflow

## Before reinstalling Windows

1. **Scan:** Replica gathers Windows, application, Store/MSIX, environment, PATH, font, development-tool, and built-in plugin inventory without mutating the PC.
2. **Review:** The user sees partial failures, unsupported applications, sensitive exclusions, and restore-confidence information.
3. **Choose a snapshot:** Lightweight for inventory, Recovery for selected files/settings, or Offline Recovery Pack for exceptional offline installer needs.
4. **Select data:** The user explicitly chooses folders and files. Replica estimates size, flags risky selections, and records exclusions.
5. **Create:** Replica builds a temporary archive, hashes content, validates the completed archive, and atomically moves it to a new destination. Existing snapshots are never silently overwritten.
6. **Protect and copy:** The user optionally encrypts supported payloads and stores the file on selected removable or cloud-synchronized storage. Replica checks write access, free space, file-system limits, collision safety, and the copied SHA-256 before finalizing the name. It recommends retaining an independent copy.
7. **Pre-reset check:** Replica opens and validates the exported copy, shows external/independent storage status, reminds the user that encryption passwords cannot be recovered, lists post-reset accounts, and explains how to obtain `ReplicaSetup.exe`.
8. **Optional installer retention:** With separate approval, Replica can store the latest stable or an explicitly selected official GitHub Release `ReplicaSetup.exe` outside the Snapshot. It verifies the declared size and available release hash and never executes the installer automatically.

## After reinstalling Windows

1. Install `ReplicaSetup.exe` from the official GitHub Release.
2. Open the snapshot. Replica validates extension, archive shape, schema compatibility, checksums, entry limits, paths, and optional encryption before exposing recovery actions.
3. Scan the current PC and retain partial-failure warnings.
4. Match applications and compute typed differences and confidence.
5. Choose Safe, Recommended, or Exact mode and select desired differences.
6. Generate a restore-plan dependency graph. Invalid cycles or unsupported dependencies block execution.
7. Review the dry run: original/current/target values, risk, elevation, restart, rollback, download size, expected duration, and manual actions.
8. Confirm the plan. Ordinary work remains unelevated; only the administrator subset is handed to a short-lived elevated instance.
9. Before each mutation, record and verify the rollback journal entry. Apply, verify, and report the result.
10. Rescan affected areas and display remaining differences, restart requirements, and manual follow-up.

The WPF implementation exposes these phases as a persisted 19-step wizard. It supports explicit drive mapping, hardware-dependent warnings, checksum-protected resume state, failed-action-only retries, and before/after similarity. See [Post-reset recovery wizard](RECOVERY_WIZARD.md).

Replica never reboots Windows automatically. If a restart is required, login-time resume registration requires separate approval, uses the same executable, and still asks the user whether to continue after login. Completed actions are not scheduled again.

## Conflict behavior

Existing files default to keeping the current copy or prompting the user. Supported options are keep current, replace with snapshot copy, rename and keep both, keep newest, or ask for every conflict. Destructive resolution is never inferred from Exact mode.

Settings merge only through a format-aware handler. Unknown JSON, registry, or configuration formats become manual instructions rather than generic overwrites.

## Failure and cancellation

A scan may complete with provider-specific warnings. A snapshot write is all-or-nothing at the destination. A restore action failure prevents dependent actions from running but does not automatically cancel independent, already approved actions unless policy requires it.

Cancellation stops scheduling new work, asks active handlers to stop, and preserves completed journal records. The result distinguishes succeeded, failed, skipped, cancelled, restart-required, and rolled-back actions. The user may retry a newly generated plan after a fresh scan.

## Rollback

Rollback affects only settings, environment entries, PATH, registry values, selected files, and other changes that Replica explicitly journaled. Replica does not automatically uninstall applications it installed. Rollback itself has a preview, integrity validation, progress, verification, and partial-failure reporting.
