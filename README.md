# Replica

> Clone your setup, not your files.

Replica is a Windows and macOS environment snapshot, migration, and recovery tool. Windows restores only what is missing or different; the current macOS Preview safely scans and creates Lightweight Snapshots without changing the Mac.

[![CI](https://github.com/HechoLP/Replica/actions/workflows/ci.yml/badge.svg)](https://github.com/HechoLP/Replica/actions/workflows/ci.yml)

On Windows, Replica records the parts of an environment that can be safely reconstructed, compares a Snapshot with the current computer, and turns approved differences into a typed restore plan. On macOS, the Preview records application bundles, Homebrew inventory, non-sensitive environment/PATH data, and font metadata in a validated `.replica` file. It is not a disk image, a full-profile backup, a credential migrator, or an unattended cloning tool.

> [!IMPORTANT]
> Replica is currently pre-release software. Review the [current limitations](#current-limitations) before using it with important data, and test recovery on non-critical data first.

## Before and after a Windows reset

Before resetting Windows, create a Recovery Snapshot, verify its SHA-256, test that Replica can open it, and copy it to an external or separately synchronized location. Replica can record installed applications, supported settings, non-sensitive environment data, and only the user folders selected during Snapshot creation.

After the reset, open Replica and choose **Post-reset recovery**. Replica validates the archive, scans the current computer without changing it, checks hardware and drive differences, shows a comparison, and creates a restore plan. Nothing changes until the user reviews the Dry Run and gives final approval. Supported changes are journaled before execution and verified afterward.

See the [recovery workflow](docs/RECOVERY_WIZARD.md) for restart/resume, drive mapping, file conflicts, and manual authentication boundaries.

## Diff Restore

Diff Restore compares the Snapshot with the current computer instead of overwriting the environment wholesale. It reports missing or extra applications, version and configuration differences, changed files, conflicts, unsupported items, sensitive exclusions, and manual steps. Extra applications are kept by default and are not automatically removed.

Restore plans use one of three review policies:

| Mode | Default behavior |
| --- | --- |
| **Safe** | Select missing, low-risk items; keep extras; block downgrades; ask on file conflicts. |
| **Recommended** | Add compatible updates, supported settings, and environment merges while retaining safety gates. |
| **Exact** | Show every actionable difference, but keep removal, downgrade, and high-risk work manual and unselected. |

Read [Diff Restore](docs/DIFF_RESTORE.md) for difference and similarity-score semantics.

## Snapshot types

All Snapshot types use the versioned, ZIP-based `.replica` format with structure, path, resource-limit, and checksum validation.

| Type | Intended contents |
| --- | --- |
| **Lightweight Snapshot** | Inventory, supported settings, exclusions, and recovery metadata; no personal files or application installers. |
| **Recovery Snapshot** | Lightweight contents plus only explicitly selected folders and files. |
| **Offline Recovery Pack** | Recovery contents plus explicitly selected offline installers whose Windows Authenticode trust, publisher certificate, SHA-256, size, provenance, architecture, and licensing warning have been reviewed. |

Offline installers are never assumed to be redistributable. Windows accepts only trusted signed installers, revalidates their identity while writing and exporting, and never runs an exported installer automatically. See [Snapshot types](docs/SNAPSHOT_TYPES.md).

## Supported capabilities

The current source implements:

- read-only Windows, winget, uninstall-registry, MSIX/Store, environment/PATH, font, and built-in plugin discovery;
- hardened `.replica` writing and reading, optional password-based AES-GCM encryption, SHA-256 checksums, cancellation, and atomic output;
- application identity matching, version comparison, typed differences, and deterministic similarity scores;
- reviewed Safe, Recommended, and Exact restore plans with dependency validation and Dry Run summaries;
- allow-listed winget, environment/PATH, registry, and selected-file restore handlers with narrow same-executable elevation;
- rollback journals for Replica-owned file, environment, PATH, registry, and explicitly supported plugin changes;
- post-reset recovery, Snapshot history and comparison, portable export, and GitHub Releases update checks;
- reviewed Offline Recovery Pack authoring and post-reset verified installer export without automatic execution;
- Korean WPF UI with persisted Light/Dark themes, task-oriented keyboard navigation, high-contrast support, and asynchronous cancellable long-running commands. The partial English resource dictionary is not offered as a complete language option.
- macOS 13+ Avalonia Preview for Apple Silicon and Intel Macs, with read-only application-bundle/Homebrew/environment/PATH/font scanning, Lightweight Snapshot creation and validation, and GitHub Release update lookup.

Automated tests use fakes and temporary data. They do not install software or change the real registry, environment, PATH, fonts, or user files.

## Built-in program support

Replica ships a fixed built-in catalog; users do not install a separate plugin manager.

- Developer tools: Visual Studio Code, Git, PowerShell, Windows Terminal, Node.js, and Python.
- Applications: PowerToys, Everything, OBS Studio, Minecraft, Docker Desktop, and Ableton Live.

Support means allow-listed inventory and configuration for recognized versions, not a complete copy of every program directory. Unsupported versions and ambiguous matches remain visible for manual review. Licensed software, authentication, large content libraries, game binaries, and other excluded payloads are not silently captured or restored. Details are in the [built-in plugin model](docs/PLUGIN_MODEL.md).

## Security and privacy exclusions

Replica does not collect or restore passwords, cookies, login sessions, OAuth or API tokens, credential values, SSH private keys, BitLocker recovery keys, payment data, browser profiles, private-key certificates, OBS stream keys, launcher authentication, or license/activation state.

It also does not implicitly sweep all of AppData, Windows, Program Files, ProgramData, caches, logs, temporary data, or large game installation folders. Recovery files must be explicitly selected. Archive extraction rejects traversal, absolute paths, duplicate entries, unsafe links/reparse points, and decompression-limit violations.

See the [security model](docs/SECURITY.md), [threat model](docs/THREAT_MODEL.md), and [security policy](SECURITY.md).

The latest completed audit stages and remaining platform gaps are tracked in [Cross-platform quality status](docs/CROSS_PLATFORM_QUALITY.md).

## Installation and GitHub Releases

[GitHub Releases](https://github.com/HechoLP/Replica/releases) is the only official distribution channel. Download only the one installer that matches the computer:

| Computer | Download |
| --- | --- |
| Windows 11 x64 | **`ReplicaSetup.exe`** |
| Apple Silicon Mac (M1 or later) | **`Replica-macOS-arm64.dmg`** |
| Intel Mac | **`Replica-macOS-x64.dmg`** |

- The automatically generated **Source code (zip)** and **Source code (tar.gz)** files are not installers.
- **Stable** Releases are recommended for normal use.
- **Alpha** and **Beta** Releases are test versions and may contain incomplete or changing behavior.
- Stable release automation requires protected Windows Authenticode and Apple Developer ID/notarization credentials. An allowed prerelease without those credentials is clearly marked **Unsigned** on Windows and ad-hoc signed but **not notarized** on macOS. Verify the matching `.sha256` file before opening a download.
- If the Releases page contains no published release, there is no official installer to download yet. Do not download executables offered through issues, pull requests, or third-party mirrors.

All installers are self-contained; users do not separately install the .NET runtime. Windows installation is documented in [Windows installer](docs/WINDOWS_INSTALLER.md), and Mac installation and Preview boundaries are documented in [macOS support](docs/MACOS.md).

## Current limitations

- Windows 11 x64 has the full migration and recovery workflow. macOS 13+ supports Apple Silicon and Intel as a read-only Preview; Recovery Snapshot creation, password entry for encrypted Snapshot opening, restore execution, rollback, and app self-update installation are not yet available in the Mac UI.
- No Stable Release or production code-signing identity has been established for this repository yet; the release workflow is prepared to fail closed until protected identities are configured.
- Hardware drivers, credentials, authentication sessions, paid licenses, private keys, browser profiles, entire WSL disks, containers, volumes, and full application directories are outside automatic recovery.
- Low-confidence package matches, unsupported versions, hardware-dependent settings, and unrecognized configuration remain manual.
- Replica never automatically removes extra applications, downgrades software, reboots Windows, or uninstalls programs during rollback.
- Recovery depends on the content actually captured, current package availability, adequate storage, and user-approved path mappings.
- A project license has not been selected; the repository is not currently offered under an open-source license.

## Roadmap

The next release gates include broader clean-VM recovery and rollback checks, production code signing, and progression through alpha, beta, and release-candidate channels before Stable. See the complete [roadmap](docs/ROADMAP.md).

## Development

Prerequisites are the .NET SDK selected by [global.json](global.json). Windows is required for WPF/Inno Setup packaging; macOS is required for final `.app`/DMG packaging. From the repository root:

```powershell
dotnet restore Replica.sln
dotnet list Replica.sln package --vulnerable --include-transitive
dotnet format Replica.sln --verify-no-changes --no-restore
dotnet build Replica.sln -c Release --no-restore
dotnet test Replica.sln -c Release --no-build
```

Building an installer additionally requires Inno Setup 6:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-release.ps1 -Version 0.2.0-alpha.3
```

On macOS, build either architecture with:

```powershell
pwsh ./scripts/build-macos-release.ps1 -Version 0.2.0-alpha.3 -Architecture arm64
```

Do not run the opt-in installer smoke test outside a clean Windows test account or disposable VM. Read [CONTRIBUTING.md](CONTRIBUTING.md), [AGENTS.md](AGENTS.md), and the [Git workflow](docs/GIT_WORKFLOW.md) before changing the repository.

## Project links

- [GitHub repository](https://github.com/HechoLP/Replica)
- [GitHub Releases](https://github.com/HechoLP/Replica/releases)
- [Changelog](CHANGELOG.md)
- [Product specification](docs/PRODUCT_SPEC.md)
- [Architecture](docs/ARCHITECTURE.md)
- [Public release process](docs/RELEASE_PROCESS.md)

## Security reporting

Do not publish vulnerabilities, malicious Snapshot samples, credentials, or user data in an issue. Follow [SECURITY.md](SECURITY.md) for private reporting guidance and safe reproduction requirements.

## License status

No license has been selected. Unless and until the owner adds a root `LICENSE`, the repository is not offered under an open-source license and no general permission to copy, modify, or distribute the code is granted. See [License decision](docs/LICENSE_DECISION.md).
