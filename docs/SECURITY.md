# Security and privacy

## Security objectives

Replica must not turn a convenience backup into a credential archive or an arbitrary code-execution channel. The core objectives are confidentiality of selected data, integrity and authenticity signals for snapshots and releases, least-privilege execution, bounded resource use, and recoverable system changes.

## Data minimization

Replica collects inventory metadata and supported settings only for declared recovery capabilities. Personal files require an explicit user selection. Broad directory selection is rejected or narrowed when it would implicitly include Windows, Program Files, ProgramData, all AppData, complete browser profiles, caches, or temporary data.

The following are excluded by default and by supported scanners: passwords, cookies, sessions, Credential Manager data, service or OAuth tokens, API keys, `.env` files, SSH private keys, BitLocker keys, stream keys, payment information, certificates with private keys, cryptocurrency wallets, and recovery phrases. A filename or variable-name match is a warning signal, not the sole defense; supported plugins must also define exact allow-lists and redaction behavior.

Logs use structured event IDs, reason codes, sizes, hashes where appropriate, and redacted paths. They do not contain secret values, file contents, command-line secrets, encryption passwords, or access tokens. Diagnostics are opt-in for sharing.

## Snapshot protection

Readers treat every archive as hostile. Before extraction or use they validate:

- `.replica` extension and ZIP structure;
- one supported, versioned manifest and required entries;
- normalized relative paths only, with no absolute, drive, UNC, device, parent-traversal, alternate-stream, or ambiguous paths;
- no duplicate/case-colliding entries, symlinks, junctions, hard-link tricks, or reparse points;
- per-entry count and size, total uncompressed size, compression ratio, and metadata depth limits;
- declared SHA-256 checksums before any payload becomes a restore source.

Extraction uses a new private temporary directory with restrictive access. The resolved path is checked to remain beneath the root immediately before creation. Payloads never execute merely because a snapshot is opened.

Optional password encryption uses an authenticated construction: AES-GCM with a fresh random nonce for every encrypted item and a key derived with PBKDF2-HMAC-SHA256, a fresh random salt, and a versioned, reviewable work factor. The password is neither stored nor logged. Authentication failure returns one generic decryption error. Encryption metadata is authenticated and designed for future parameter upgrades. Encryption does not establish who created the snapshot.

Portable export treats removable drives and synchronized folders as failure-prone destinations. Replica rejects reparse points, unsafe leaf names, insufficient space, FAT32 single-file overflow, and existing final names. It copies to a unique sibling temporary extension, flushes it, compares source and destination SHA-256 and size, and only then assigns the final `.replica` name. Synchronization completion remains a user-visible check because a local hash cannot prove that a cloud provider uploaded the bytes.

## Restore execution

All restores begin as a typed plan. There is no general `ShellCommand` action. Handlers construct explicit executable paths and argument lists for allow-listed tools, validate package identifiers and destination paths, enforce timeouts/cancellation/output limits, and verify outcomes with a rescan.

The normal UI is unelevated. Administrator work uses the same signed/identified `Replica.exe` in a separate elevated executor mode. Its plan file has restrictive ACLs, a SHA-256 digest, short expiry, one-use random token, session binding, and only administrator-required allow-listed actions. The elevated process validates all fields independently, deletes the plan after use, and does not host the normal UI.

Environment and PATH edits are structured merges, never whole-block replacement. Registry writes are limited to plugin-approved keys and value types. File restore rejects destinations outside the approved root, reparse points, oversized payloads, and checksum mismatches; it backs up existing content and performs atomic replacement.

## Rollback and retention

Before a supported mutation, Replica writes the original state to `%LOCALAPPDATA%\Replica\Rollback\<SessionId>`, flushes it, and updates checksums. A journal failure blocks the mutation. Journal access is restricted to the user and elevated executor when needed. Retention is bounded and deletion is user-visible.

Application installation is not automatically reversed by uninstalling it. The journal documents manual follow-up. Rollback never expands beyond changes recorded for that Replica session.

## Release and update security

Official downloads come from repository releases, use HTTPS, and are matched by expected owner/repository and asset name. Replica verifies SHA-256 and shows Authenticode status. Until code signing exists, the UI and release notes label the installer Unsigned.

The optional before-reset installer copy accepts only `ReplicaSetup.exe` from published `HechoLP/Replica` GitHub Releases after explicit approval. It is stored beside recovery material rather than inside a Snapshot, streams to a temporary name with bounded declared size, verifies GitHub's SHA-256 digest when present, and is never launched automatically. A missing release digest is shown as unavailable rather than treated as verification.

The in-app update workflow additionally separates lookup, download, and installer launch. Channel filtering and strict SemVer parsing prevent prerelease drift. Downloads use randomized application-owned Temp sessions, bounded response and Asset sizes, explicit timeout/cancellation, official URL and filename allow-lists, and SHA-256 agreement across available sources. Launch requires a second approval and repeats path, size, reparse-point, and hash validation. Replica passes no silent or arbitrary arguments and does not force elevation.

CI uses least-privilege tokens, pinned actions where possible, protected release environments for secrets, and immutable version tags. Dependency and installer changes require review. A failed test, packaging check, checksum step, or signing step prevents publication.

## Vulnerability reporting

Do not place exploitable details or real sensitive samples in public issues. Until a private reporting channel is configured, contact the repository owner through a private GitHub profile channel and provide only the minimum reproduction data. The project should enable GitHub private vulnerability reporting before its first public binary release.

The executable regression matrix and local verification commands are documented in [SECURITY_TESTING.md](SECURITY_TESTING.md).
