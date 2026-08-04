# Built-in developer plugins

Replica ships official developer migration support in `Replica.Plugins.BuiltIn`. It does not download, discover, or install third-party plugin assemblies. The desktop application references this project directly, so a future `ReplicaSetup.exe` contains the same fixed catalog as the application build.

## Contract and lifecycle

Each `IBuiltInPlugin` exposes a stable ID, display name, and version, then implements six cancellable operations:

1. `DetectAsync` performs read-only detection.
2. `CaptureAsync` returns allow-listed values, sanitized configuration files, explicit exclusions, and non-sensitive warnings.
3. `CompareAsync` produces deterministic typed differences. JSON files use semantic comparison rather than text formatting.
4. `BuildRestoreActionsAsync` maps differences to allow-listed restore action types and supported review strategies.
5. `ValidateAsync` captures again and reports remaining drift.
6. `GetSensitiveExclusionsAsync` explains data that is deliberately omitted.

Captured plugin data is converted to `ReplicaPluginSnapshot` and stored in `plugins/index.json`. Inline plugin paths are logical relative paths; rooted and traversal paths are rejected by the hardened snapshot reader.

## Included plugins

- Visual Studio Code: user settings, keybindings, tasks, snippets, and versioned extension identities. Authentication, global/workspace storage, history, and caches are excluded. JSON restore offers merge, replace, or keep-current review strategies.
- Git: allow-listed identity/editor/alias/default-branch configuration and only the `credential.helper` name. OAuth data, credential values, and private keys are excluded. Public-key fingerprints are opt-in and the key content is not stored.
- PowerShell: version, sanitized profiles, and installed module names and versions. Replica never registers untrusted repositories or changes execution policy.
- Windows Terminal: semantic settings capture for profiles, color schemes, defaults, and starting directories. Duplicate profile GUIDs and missing shells become warnings requiring review.
- Node.js: Node/npm versions, version-manager detection, and global package names and versions. `.npmrc`, authentication values, and caches are excluded.
- Python: interpreter inventory, pip package names and versions, and virtual-environment location metadata. Virtual-environment contents and package credentials are excluded.

## Process and file safety

The production host accepts only a closed `DeveloperToolQuery` enum. Every query maps to a fixed executable and fixed argument list, captures bounded output, has a timeout, supports cancellation, and never accepts an arbitrary command string from a snapshot or user setting. File reads are bounded, reject reparse points, and use only plugin-owned known locations. Recursive enumeration is capped.

Fixture tests provide all tool output and filesystem content in memory. They never invoke installed tools or read the developer's real profile, and every plugin verifies that credential sentinels cannot survive serialization into a `.replica` snapshot.
