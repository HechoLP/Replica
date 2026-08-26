# Built-in migration plugins

Replica ships official developer and application migration support in `Replica.Plugins.BuiltIn`. It does not download, discover, or install third-party plugin assemblies. The desktop application references this project directly, so a future `ReplicaSetup.exe` contains the same fixed catalog as the application build.

## Contract and lifecycle

Each `IBuiltInPlugin` exposes a stable ID, display name, and version, then implements six cancellable operations:

1. `DetectAsync` performs read-only detection.
2. `CaptureAsync` returns allow-listed values, sanitized configuration files, explicit exclusions, and non-sensitive warnings.
3. `CompareAsync` produces deterministic typed differences. JSON files use semantic comparison rather than text formatting.
4. `BuildRestoreActionsAsync` maps differences to allow-listed restore action types and supported review strategies.
5. `ValidateAsync` captures again and reports remaining drift.
6. `GetSensitiveExclusionsAsync` explains data that is deliberately omitted.

Captured plugin data is converted to `ReplicaPluginSnapshot` and stored in `plugins/index.json`. Inline plugin paths are logical relative paths; rooted and traversal paths are rejected by the hardened snapshot reader.

## Included developer plugins

- Visual Studio Code: an explicit schema allow-list of ordinary editor preferences plus versioned extension identities. Keybindings, tasks, snippets, authentication, global/workspace storage, history, caches, and unknown setting keys are excluded because their open-ended strings can contain commands or credentials. The projected JSON restore offers merge, replace, or keep-current review strategies.
- Git: allow-listed identity/editor/alias/default-branch configuration and only the `credential.helper` name. OAuth data, credential values, and private keys are excluded. Public-key fingerprints are opt-in and the key content is not stored.
- PowerShell: version and installed module names and versions. Profile script bodies are arbitrary code and are never captured. Replica never registers untrusted repositories or changes execution policy.
- Windows Terminal: an explicit schema allow-list of ordinary top-level preferences. Profiles, actions, command lines, and color-scheme bodies are omitted; Replica still inspects them read-only to warn about duplicate profile GUIDs and missing shells.
- Node.js: Node/npm versions, version-manager detection, and global package names and versions. `.npmrc`, authentication values, and caches are excluded.
- Python: interpreter inventory, pip package names and versions, and virtual-environment location metadata. Virtual-environment contents and package credentials are excluded.

## Included application plugins

- PowerToys: explicit boolean, numeric, and short-token preferences for general settings, FancyZones, PowerRename, and Awake. Open-ended layouts, application history, keyboard command mappings, and unknown keys are excluded.
- Everything: allow-listed search, UI, index, and exclusion settings. The generated `Everything.db` index is never captured.
- OBS Studio: explicit audio/video scalar inventory only. Scene collections, sources, hotkeys, service credentials, browser state, and unknown profile settings are not copied; restore remains manual while OBS is running.
- Minecraft: explicit numeric display/input options plus resource-pack, shader-pack, mod, and unknown-config filename/size manifests. Unknown configuration bodies, game binaries, launcher authentication, screenshots, logs, crash reports, and mod JAR payloads are excluded. World payloads remain in the Recovery Snapshot subsystem and are recognized only from explicit selected paths.
- Docker Desktop: installed version plus explicit numeric resource limits and boolean engine preferences. Per-distribution mappings, unknown settings, images, containers, volumes, registry credentials, Kubernetes secrets, and WSL virtual disks are excluded.
- Ableton Live: supported version plus exact allow-listed library/template/VST path values and a small option inventory. Opaque preference bodies, licensed Packs, plugin binaries, licenses, authorization state, large sample libraries, and projects are excluded. Selected projects use the normal Recovery Snapshot file flow.

Supported free applications produce allow-listed package-install actions when missing. Licensed applications without an allow-listed package identity remain explicit manual installation steps. Settings are restored only through the typed plan, review, journal, and validation lifecycle.

## Process and file safety

The production host accepts only a closed `DeveloperToolQuery` enum. Every query maps to a fixed executable and fixed argument list, captures bounded output, has a timeout, supports cancellation, and never accepts an arbitrary command string from a snapshot or user setting. File reads are bounded, reject reparse points, and use only plugin-owned known locations. Recursive enumeration is capped.

Fixture tests provide all tool output and filesystem content in memory. They never invoke installed tools or read the user's real profile. Every plugin verifies that both recognizable credentials and opaque values under generic `auth`/`key`-style properties cannot survive projection into a `.replica` snapshot.
