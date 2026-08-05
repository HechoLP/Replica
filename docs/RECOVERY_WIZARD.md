# Post-reset recovery wizard

The post-reset recovery wizard is the user-facing orchestrator for restoring a reviewed Recovery Snapshot or Offline Recovery Pack. It reuses the existing snapshot reader, read-only scanner, diff engine, restore planner, controlled executor, verification pass, and rollback journal. A Lightweight Snapshot cannot start this workflow.

## Workflow

The persisted state machine has 19 visible steps:

1. select a Recovery Snapshot;
2. validate archive structure, schema, paths, and checksums;
3. request a password when encrypted;
4. show the source computer details;
5. scan the current computer without mutation;
6. compare Windows and hardware compatibility;
7. analyze application differences;
8. analyze setting differences;
9. confirm selected-file destinations;
10. create a typed restore plan;
11. obtain final user approval;
12. install approved applications;
13. restore approved settings;
14. restore explicitly selected user files;
15. show restart requirements;
16. resume only after login and renewed confirmation;
17. run final validation;
18. show before/after environment similarity;
19. show remaining manual work.

Passwords are held only in a caller-owned character buffer, cleared after each operation, and never written to session state or logs. A corrupt archive or incorrect password produces a generic error that does not reveal cryptographic details.

## Paths and conflicts

Every selected source folder has an explicit mapping containing the source path, target path, scope, conflict policy, and estimated bytes. Removing a mapping excludes that folder from restoration. A mapping cannot escape its target root, and materialized payloads reject archive traversal, undeclared entries, checksum mismatches, duplicate entries, absolute paths, link-like entries, and reparse points.

The default conflict behavior is `RenameAndKeepBoth`. Other supported policies remain keep existing, overwrite with the snapshot, keep newest, or prompt per conflict. The wizard checks available space on mapped target drives before plan approval.

## Hardware changes

The compatibility result compares recorded and current graphics, display, audio, and drive information. A changed GPU never creates an old-driver restore action; the result instead directs the user to the current hardware vendor's official installer. Display resolution, audio-device, and missing-drive differences mark hardware-dependent settings for review.

## Restart and resume

Replica never initiates an automatic reboot. When an approved action reports a restart requirement, the user may explicitly register a single per-user login command for the same `Replica.exe`:

```text
Replica.exe --resume-recovery <SessionId>
```

The argument parser accepts only the exact switch and a 32-character GUID. Registration is limited to the exact `HKCU` Run value `ReplicaRecoveryResume`, and the registry change is journaled before mutation. The session is checksum-protected and atomically stored under `%LOCALAPPDATA%\Replica\Recovery\Sessions\<SessionId>\session.json`.

After login, the normal unelevated UI opens and asks whether to continue. Completed action identifiers are persisted and are never scheduled again. Failed actions can be retried as a restricted subset. Resume registration is removed before final verification or cancellation.

## Authentication boundary

Replica does not copy, bypass, or automate Microsoft, Steam, Epic, Xbox, Adobe, Ableton, VPN, browser, or other authentication. It does not restore private SSH keys or paid VST licenses. Applicable items appear in the final manual-work list with official sign-in or vendor-install guidance.

## Result

The final view shows before/after similarity, successful application installs, successful setting and selected-file restores, failures, skipped actions, restart state, and manual-action count. Partial failure is preserved as action-level evidence for retry or rollback.
