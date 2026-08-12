# Changelog

All notable changes to Replica are documented in this file. The project follows semantic version tags for GitHub Releases.

## [Unreleased]

### Added

- macOS 13+ Preview built with Avalonia for Apple Silicon and Intel Macs.
- Read-only application bundle, Homebrew, environment/PATH, and font inventory on macOS.
- macOS Lightweight Snapshot creation and hardened cross-platform Snapshot validation.
- Self-contained `.app`/DMG packaging, architecture-specific SHA-256 files, and multi-platform GitHub Release automation.

### Security

- Mac snapshots remove sensitive environment values and exclude Keychain, broad Library data, browser profiles, and user files.
- Windows restore handlers are not registered or executable in the Mac Preview.
- macOS packages are plainly described as ad-hoc signed and not notarized.

## [0.1.0-alpha.1] - 2026-08-12

This is the first experimental Windows 11 `win-x64` Alpha candidate. It is not a disk image, credential migrator, full-profile backup, or unattended cloning tool.

### Added

- Windows 11 .NET 10 WPF application shell, localization, themes, accessibility structure, navigation, and global error handling.
- Hardened `.replica` Snapshot format with checksums, optional encryption, selected-file recovery, and portable export.
- Read-only Windows environment scanning, application matching, Diff Restore, similarity scoring, and typed Dry Run planning.
- Controlled restore execution, narrow same-executable elevation, rollback journaling, restart/resume recovery, and verification.
- Built-in developer and application migration plugins with documented sensitive-data exclusions.
- SQLite Snapshot history, Snapshot comparison, past-state planning, and restore/rollback records.
- GitHub Releases update service, self-contained Windows installer build, CI, CodeQL, Dependabot, and tag-driven Release automation.

### Security

- Added archive path, entry, size, compression, JSON, checksum, and reparse-point defenses.
- Added typed elevated-plan validation, expiry, integrity, single-use, and least-privilege boundaries.
- Added redaction and regression tests for secrets, user data, untrusted plugin artifacts, recovery paths, and update Assets.
- Added an offline Windows Sandbox installer smoke test that maps the repository read-only and verifies install, launch, `.replica` association, upgrade, uninstall, and user-data retention without changing the host installation.

### Known issues

- The candidate is unsigned. Windows may display an unknown-publisher warning; verify the published SHA-256 before testing it.
- Windows 11 `win-x64` is the only supported client target in this release.
- Low-confidence package matches, unsupported application versions, hardware-dependent settings, credentials, paid licenses, and unrecognized configuration remain manual actions.
- Replica never automatically removes extra applications, downgrades software, reboots Windows, or uninstalls applications during rollback.
- Hardware drivers, browser profiles, authentication sessions, private keys, containers, volumes, complete WSL disks, and broad application directories are not restored.
- A full human-driven scan-to-recovery-and-rollback walkthrough and accessibility review in a clean Windows 11 VM remain release-readiness blockers even though automated scenarios and the isolated installer smoke test pass.
- GitHub Private Vulnerability Reporting and the project license decision remain open.

Release notes and downloadable Assets will appear on [GitHub Releases](https://github.com/HechoLP/Replica/releases).
