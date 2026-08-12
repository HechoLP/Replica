# Roadmap

The order below builds read-only foundations before mutation. Each milestone lands through a focused pull request with tests and updated security documentation.

## 0. Project foundation

Define product boundaries, architecture, snapshot types, recovery and diff policies, security model, Git/release workflow, and architectural decisions. No application code.

## 1. Desktop bootstrap

Create the .NET 10 WPF solution, dependency injection, navigation/dialog/version/compatibility/path services, global error handling, localization/theme structure, placeholder commands, tests, and Windows CI. No real machine changes.

## 2. Snapshot format

Implement versioned ZIP storage, domain models, atomic writer, hardened reader, checksums, explicit file selection, optional authenticated encryption, resource limits, and security tests.

## 3. Windows scanner

Implement read-only winget, uninstall registry, MSIX, environment/PATH, Windows, font, and built-in plugin scanners with timeouts, cancellation, redaction, partial-failure reporting, and UI progress.

## 4. Application matching

Implement ordered identities, name normalization, confidence, version/channel comparison, duplicate merging, and unit tests. Low/Unknown matches remain manual.

## 5. Diff engine

Compare snapshot and target inventories, compute explainable typed differences and coverage-aware similarity scores, and provide a filterable viewer.

## 6. Restore planner and dry run

Create typed restore actions, dependency DAG validation, Safe/Recommended/Exact policies, deterministic plan summaries, risk/elevation/restart/rollback data, and an approval UI. Still no actual mutation.

## 7. Controlled restore executor

Add allow-listed handlers for supported winget, environment, registry, and file operations; narrow same-executable elevation; preflight checks; progress/cancellation; post-action verification; and mock-based tests.

## 8. Rollback engine

Journal original state before every supported mutation, verify journal integrity, preview and apply rollback, retain partial results, and prohibit automatic software uninstall.

## 9. Product hardening

Complete history/settings/about UX, accessibility, localization, update checks, diagnostics redaction, performance limits, parser fuzzing, migration testing, installer/uninstaller behavior, and end-to-end clean-VM validation.

## 10. Release channels

Publish `v0.1.0-alpha.1`, collect opt-in diagnostics and compatibility feedback, progress through beta and RC exit criteria, enable private vulnerability reporting, establish code signing, and publish stable only after recovery and rollback success criteria are met.

## 11. macOS Preview

Extract portable Core and Snapshot storage, add an Avalonia app for macOS 13+, inventory application bundles/Homebrew/environment/PATH/fonts without mutation, create and validate Lightweight Snapshots, and publish self-contained ARM64/x64 DMGs. Typed macOS restore planning, journaling, encryption-password UI, notarization, and recovery remain later gated milestones.

## Milestone gates

- No writer ships before the planner and dry-run review.
- No mutation ships without journal-before-change behavior and mock-based failure tests.
- No public installer ships unless CI builds, tests, packages, and checksums `ReplicaSetup.exe` plus both macOS architecture DMGs.
- No stable release ships without signed artifacts or an explicitly approved, prominently documented exception.
- Unsupported recovery stays visible and manual; roadmap pressure does not justify silent best-effort mutation.
