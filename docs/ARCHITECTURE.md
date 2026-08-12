# Architecture

## Principles

Replica separates observation from mutation and policy from mechanism. Scanning and diffing are read-only. A restore planner turns differences into typed actions. The executor accepts only a validated, user-approved plan. Each mutation is journaled before application and verified afterward.

The user sees and installs one application for the selected platform. Internal class libraries ship inside `ReplicaSetup.exe`/`Replica.exe` on Windows or `Replica.app` inside the matching macOS DMG. No separately installed helper or service is introduced.

## Planned solution boundaries

```text
Replica.App                 WPF views, view models, navigation, dialogs, DI bootstrap
Replica.Core                Domain models, policies, matching, diffing, planning contracts
Replica.Infrastructure      Windows scanners, storage, SQLite, process and restore adapters
Replica.Plugins.BuiltIn     Allow-listed application and development-tool integrations
Replica.Storage             Cross-platform hardened .replica reader and writer
Replica.Mac                 Avalonia macOS Preview UI and DI bootstrap
Replica.Mac.Infrastructure  Read-only macOS scanners, paths, Snapshot composition, updates

Replica.Core.Tests
Replica.Infrastructure.Tests
Replica.IntegrationTests
Replica.Mac.Tests
```

Dependencies point inward: platform apps and infrastructure depend on Core abstractions; Core targets portable `net10.0` and does not depend on WPF, Avalonia, SQLite, winget, Homebrew, the registry, or filesystem-specific implementations. The hardened Snapshot storage project is also platform-neutral. Built-in Windows plugins implement stable Core contracts and are registered by the Windows App bootstrapper.

## Major subsystems

- **Bootstrap and shell:** dependency injection, navigation, dialogs, localization, theme, global exception handling, version information, and Windows compatibility checks.
- **WPF workspace:** a single unelevated Shell hosts dedicated Home, Scan, Snapshot Builder, Comparison, Diff, Restore Plan, Recovery, Execution, Result, History, Settings, About, and Update views. View models own state and commands; code-behind only initializes controls.
- **macOS Preview workspace:** a single Avalonia Shell provides read-only scanning, Lightweight Snapshot creation/validation, Snapshot inspection, and Release lookup. It never registers or resolves Windows mutation handlers.
- **Inventory:** coordinates Windows, application, winget, registry, MSIX, environment, font, and plugin scanners. Partial failures become warnings rather than hidden omissions.
- **Snapshot storage:** writes and validates atomic ZIP-based `.replica` containers, manifests, checksums, optional encrypted payloads, and explicit exclusions.
- **Portable export:** inspects user-selected removable, fixed, network, and synchronized destinations, copies through a sibling temporary extension, verifies source/destination SHA-256, and never overwrites a collision.
- **Snapshot history:** indexes portable Snapshot metadata, comparison keys, audit summaries, and matching overrides in local SQLite without storing archive bodies or selected-file contents.
- **Matching and diff:** maps inventory identities with confidence levels, compares versions and settings, and emits typed differences without changing the computer.
- **Planning:** converts chosen differences and restore mode into a dependency DAG of typed actions. Cycles make the plan invalid.
- **Execution:** dispatches allow-listed action handlers, reports progress, honors cancellation and timeouts, and never interprets arbitrary command text.
- **Elevation:** restarts the same `Replica.exe` for the administrator-only subset of an approved, integrity-protected, short-lived plan.
- **Rollback:** journals original state before every supported mutation and restores only changes made by Replica.
- **Verification:** rescans relevant targets and records action outcomes, warnings, restarts, and manual follow-up.
- **Updates:** queries bounded GitHub Releases metadata, applies Stable/Beta/Alpha semantic-version policy, persists exact skipped tags, reports Rate Limits, and downloads only with user consent. Download verification and installer launch approval remain separate.
- **Recovery installer retention:** downloads only the exact official `HechoLP/Replica` release asset after approval, stores it outside the Snapshot, verifies size and available SHA-256 metadata, and never executes it.

## End-to-end data flow

```text
scan -> inventory -> snapshot writer -> .replica
                              |
.replica -> validation -> target scan -> matcher/diff -> plan -> dry run/approval
                                                      -> journal -> execute -> verify
                                                                   -> rollback (optional)
```

No edge from scan, validation, matching, or diffing may reach a system writer.

## Persistence

Application-owned data lives below `%LOCALAPPDATA%\Replica` with paths provided by one `ReplicaPathProvider` abstraction:

- logs;
- local SQLite database;
- snapshots chosen for local storage;
- recovery working data;
- rollback sessions;
- temporary files.

User-selected snapshot destinations remain user-controlled. Database data indexes local history and state; the portable source of recovery truth is the validated snapshot plus the current scan. Temporary and rollback data have bounded retention policies and are never committed.

On macOS, application-owned Preview data uses the platform `LocalApplicationData` location under `Replica`. The Mac Preview does not create rollback or recovery execution state because it performs no system mutations.

## Concurrency and resilience

All I/O and process operations are asynchronous, cancellable, and progress-reporting. External process execution has explicit argument construction, timeout, output bounds, exit-code capture, and process-tree cancellation. The UI thread only coordinates state and presentation.

Long operations produce stable session identifiers. Atomic writes prevent a cancelled snapshot from replacing an existing file. Partial scan results identify the failed provider. Restore dependencies skip unsafe downstream actions after failure while leaving independent actions eligible.

## Trust boundaries

Snapshots, installers, GitHub responses, process output, registry data, and plugin data are untrusted inputs. Validation occurs at each adapter boundary. Elevated execution receives a narrow, hashed, expiring plan containing only allow-listed action types. See [SECURITY.md](SECURITY.md) and [THREAT_MODEL.md](THREAT_MODEL.md).
