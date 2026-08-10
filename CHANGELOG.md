# Changelog

All notable changes to Replica will be documented in this file. The project follows semantic version tags for GitHub Releases, but it has not published a Release yet.

## [Unreleased]

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

### Known release blockers

- Stable release qualification, clean-VM validation, production code signing, private vulnerability reporting, and the project license decision remain open.

Release notes and downloadable Assets will appear on [GitHub Releases](https://github.com/HechoLP/Replica/releases).
