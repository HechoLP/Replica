# macOS support

Replica for macOS is a read-only Preview for macOS 13 or later. GitHub Releases publishes a self-contained DMG for Apple Silicon (`Replica-macOS-arm64.dmg`) and Intel (`Replica-macOS-x64.dmg`). The user installs one `Replica.app`; no helper, service, plugin manager, or separate .NET runtime is installed.

## Current capabilities

- read application bundles from `/Applications` and `~/Applications` without launching them;
- read Homebrew formula and cask inventory through fixed, typed `brew list --versions` operations with timeout, cancellation, bounded output, and captured exit status;
- read process environment and ordered PATH entries while removing sensitive values;
- inventory user and system font file metadata without copying commercial font files;
- create an atomic, checksummed Lightweight `.replica` Snapshot;
- validate and inspect unencrypted Windows or macOS Snapshots with the same archive, path, size, duplicate-entry, compression, JSON, and checksum defenses;
- check the official `HechoLP/Replica` GitHub Releases catalog and select the matching Mac architecture Asset.

The Mac Preview never installs or removes applications, changes environment variables, writes system settings, restores files, requests administrator rights, or executes a Windows restore action. Cross-platform Snapshot inspection is informational; an operating-system mismatch is not permission to translate or run restore operations.

## Privacy

Environment names containing token, secret, password, key, credential, or connection-string markers are recorded only as exclusions; their values are discarded before Snapshot construction. Keychain data, browser profiles, login sessions, private keys, all of `~/Library`, and user files are not collected. Lightweight Snapshots contain no selected-file payload.

## Installation

Download the matching DMG and `.sha256` file from [GitHub Releases](https://github.com/HechoLP/Replica/releases). Verify SHA-256, open the DMG, and drag `Replica.app` to Applications.

The Preview build is ad-hoc signed for bundle integrity and is not Apple Developer ID signed or notarized. macOS Gatekeeper may require the user to confirm opening the app through Finder. The project does not tell users to disable Gatekeeper globally.

## Packaging

`scripts/build-macos-release.ps1` runs only on macOS. It publishes `Replica.Mac` as a .NET 10 self-contained single-file executable, constructs `Replica.app`, writes a versioned `Info.plist` with `.replica` association, applies an explicitly ad-hoc signature, verifies the bundle, creates a compressed DMG, and emits a SHA-256 file. The tag-driven Release workflow builds and tests ARM64 and x64 packages independently before publication.

Recovery Snapshot creation, encryption-password UI, Diff Restore, typed macOS restore planning, journaling, rollback, notarized distribution, and in-app update download/install are future security-reviewed work and are not represented as complete.
