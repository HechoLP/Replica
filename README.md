# Replica

> Clone your Windows setup, not your files.

Replica is a planned Windows 11 desktop application for recording a machine's recoverable setup, comparing it with another installation, and restoring only the items a user approves. It is not a disk image, credential migrator, or unattended PC cloning tool.

The product will capture installed applications and versions, winget and Store identities, selected application settings, environment variables and PATH, development tooling, fonts, and explicitly selected files. A user can save that inventory as a `.replica` snapshot, open it after reinstalling Windows, review a diff, run a dry-run restore plan, apply supported actions, and verify or roll back Replica-owned changes.

## Snapshot choices

- **Lightweight Snapshot** records inventory and settings but does not include application installers or personal files.
- **Recovery Snapshot** adds only user-selected folders and files plus recovery preferences.
- **Offline Recovery Pack** may also contain explicitly selected installers for software that cannot conveniently be fetched again. It is intentionally not the default because it can be very large.

See [Snapshot types](docs/SNAPSHOT_TYPES.md) for the exact boundaries.

## Safety model

Replica defaults to Safe restore mode. It never automatically removes extra applications, automatically downgrades software, collects credentials, or sweeps broad personal-data locations. Every system change is represented in a typed restore plan, previewed as a dry run, journaled before execution, and verified afterward. Administrative work is isolated to approved actions executed by the same application in a short-lived elevated mode.

## Distribution

The official distribution channel is GitHub Releases for `HechoLP/Replica`. End users install one file:

```text
ReplicaSetup.exe
```

The planned desktop stack is C#, .NET 10 LTS, WPF/MVVM, SQLite, and a self-contained `win-x64` build packaged with Inno Setup. Replica's internal libraries are implementation details and are not separate user installs.

## Project status

Replica now has a compilable .NET 10 WPF desktop bootstrap, a versioned and hardened `.replica` snapshot format, a read-only Windows environment scanner, explainable application identity and version matching, and a deterministic read-only diff engine with weighted similarity scores and safety metadata. A typed restore planner converts reviewed differences into an allow-listed dependency DAG, blocks unsafe defaults, and presents a Dry Run for explicit approval in the WPF shell. The controlled executor can apply approved winget, environment/PATH, allow-listed registry, and explicitly mapped file actions with cancellation, verification, journal-before-change enforcement, and narrow same-executable elevation. A checksum-protected rollback journal can preview and reverse only Replica-owned file, environment, PATH, registry, and explicitly supported plugin changes; application installation remains a manual follow-up and is never automatically undone. Built-in plugins capture and compare sanitized developer configuration plus PowerToys, Everything, OBS Studio, Minecraft, Docker Desktop, and Ableton Live settings without requiring a separate plugin installation. Automatic removal, automatic downgrade, and arbitrary command execution remain unsupported. The staged delivery plan is in [ROADMAP.md](docs/ROADMAP.md).

## Documentation

- [Product specification](docs/PRODUCT_SPEC.md)
- [Architecture](docs/ARCHITECTURE.md)
- [Recovery workflow](docs/RECOVERY_WORKFLOW.md)
- [Diff restore](docs/DIFF_RESTORE.md)
- [Built-in developer plugins](docs/PLUGIN_MODEL.md)
- [Security](docs/SECURITY.md) and [threat model](docs/THREAT_MODEL.md)
- [Git workflow](docs/GIT_WORKFLOW.md) and [release process](docs/RELEASE_PROCESS.md)
- [License decision](docs/LICENSE_DECISION.md)

## Contributing

Read [AGENTS.md](AGENTS.md) and [GIT_WORKFLOW.md](docs/GIT_WORKFLOW.md) before making changes. System-changing behavior requires a dry run, rollback journal design, least-privilege review, and tests that use mocks rather than the real Windows environment.

## License

No license has been selected yet. Until the repository owner makes and records that decision, the contents are not offered under an open-source license. See [LICENSE_DECISION.md](docs/LICENSE_DECISION.md).
