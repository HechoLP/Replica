# macOS support

Replica for macOS is a read-only Preview for macOS 13 or later. GitHub Releases publishes a self-contained DMG for Apple Silicon (`Replica-macOS-arm64.dmg`) and Intel (`Replica-macOS-x64.dmg`). The user installs one `Replica.app`; no helper, service, plugin manager, or separate .NET runtime is installed.

## Current capabilities

- read bounded, nested application bundles from `/Applications`, `~/Applications`, and `/System/Applications` without launching them, including binary `Info.plist` metadata through the fixed system `plutil` tool;
- read Homebrew formula and cask inventory through fixed, typed `brew list --versions` operations with timeout, cancellation, bounded output, and captured exit status;
- read ordered PATH entries and retain values only for a small non-sensitive environment allow-list; all other environment names are recorded as exclusions without their values;
- inventory user and system font file metadata without copying commercial font files;
- create an atomic, checksummed Lightweight `.replica` Snapshot;
- validate and inspect unencrypted Windows or macOS Snapshots with the same archive, path, size, duplicate-entry, compression, JSON, and checksum defenses;
- open an associated `.replica` document from Finder through the same validated inspection path used by the file picker;
- check the exact official `HechoLP/Replica` GitHub Releases paths using Stable as the default channel and select the matching Mac architecture Asset.

The Mac Preview never installs or removes applications, changes environment variables, writes system settings, restores files, requests administrator rights, or executes a Windows restore action. Cross-platform Snapshot inspection is informational; an operating-system mismatch is not permission to translate or run restore operations.

## Privacy

Only explicitly allow-listed display and tool-selection variables such as `LANG`, `LC_*`, `SHELL`, `TERM`, `EDITOR`, and `HOMEBREW_PREFIX` may retain values. Every other process-environment value is discarded before Snapshot construction; token, authentication, cookie, JWT, password, key, credential, and connection-string names receive sensitive exclusion reasons. Keychain data, browser profiles, login sessions, private keys, all of `~/Library`, and user files are not collected. Lightweight Snapshots contain no selected-file payload.

## Installation

Download the matching DMG and `.sha256` file from [GitHub Releases](https://github.com/HechoLP/Replica/releases). Verify SHA-256, open the DMG, and drag `Replica.app` to Applications.

The Preview build is ad-hoc signed for bundle integrity and is not Apple Developer ID signed or notarized. macOS Gatekeeper may require the user to confirm opening the app through Finder. The project does not tell users to disable Gatekeeper globally.

## Packaging

`scripts/build-macos-release.ps1` runs only on macOS. It publishes `Replica.Mac` as a .NET 10 self-contained single-file executable, constructs `Replica.app`, writes and lints a versioned `Info.plist` with `.replica` association, verifies the exact Mach-O architecture and bundle metadata, applies an explicitly ad-hoc signature, creates and read-only mounts a verified DMG, checks the packaged app again, and emits a SHA-256 file. Pull-request CI and the tag-driven Release workflow both package ARM64 and x64 before publication. CI cross-publishing does not replace a human launch check on representative Intel hardware.

Recovery Snapshot creation, encryption-password UI, Diff Restore, typed macOS restore planning, journaling, rollback, notarized distribution, and in-app update download/install are future security-reviewed work and are not represented as complete.
