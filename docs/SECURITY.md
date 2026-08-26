# Security and privacy

## Security objectives

Replica must not turn a convenience backup into a credential archive or an arbitrary code-execution channel. The core objectives are confidentiality of selected data, integrity and authenticity signals for snapshots and releases, least-privilege execution, bounded resource use, and recoverable system changes.

## Data minimization

Replica collects inventory metadata and supported settings only for declared recovery capabilities. Personal files require an explicit user selection. Broad directory selection is rejected or narrowed when it would implicitly include Windows, Program Files, ProgramData, all AppData, complete browser profiles, caches, or temporary data.

The following are excluded by default and by supported scanners: passwords, cookies, sessions, Credential Manager data, service or OAuth tokens, API and access keys, `.env` files, SSH private keys, BitLocker keys, stream keys, payment information, certificates with private keys, cryptocurrency wallets, and recovery phrases. A filename or variable-name match is a warning signal, not the sole defense. Built-in plugins project only plugin-specific, exact setting keys with constrained value types. Open-ended files such as PowerShell profiles, VS Code tasks/keybindings/snippets, OBS scenes, unknown Minecraft configuration, and opaque Ableton settings are not copied at all. Credential heuristics remain defense in depth for already allow-listed scalar values, not the authorization boundary.

On macOS, Keychain data, broad `~/Library` capture, browser profiles, login state, and user files are outside the Preview scanner. Environment values whose names indicate tokens, secrets, passwords, keys, credentials, or connection strings are discarded before Snapshot construction and appear only as reason-coded exclusions.

Logs use structured event IDs, reason codes, sizes, hashes where appropriate, and redacted paths. They do not contain secret values, file contents, command-line secrets, encryption passwords, or access tokens. Diagnostics are opt-in for sharing.

## Snapshot protection

Readers treat every archive as hostile. A bounded ZIP/ZIP64 end-record and central-directory preflight rejects excessive physical size, central-directory bytes, multi-disk archives, and entry counts before `ZipArchive` materializes attacker-controlled records. Before extraction or use they then validate:

- `.replica` extension and ZIP structure;
- one supported, versioned manifest and required entries;
- normalized relative paths only, with no absolute, drive, UNC, device, parent-traversal, alternate-stream, or ambiguous paths;
- no duplicate/case-colliding entries, symlinks, junctions, hard-link tricks, or reparse points;
- per-entry count and size, total uncompressed size, compression ratio, and metadata depth limits;
- declared SHA-256 checksums before any payload becomes a restore source.

Extraction uses a new private temporary directory with restrictive access. The resolved path is checked to remain beneath the root immediately before creation. Payloads never execute merely because a snapshot is opened.

Optional password encryption uses an authenticated construction: AES-GCM with a fresh random nonce for every encrypted item and a key derived with PBKDF2-HMAC-SHA256, a fresh random salt, and a versioned, reviewable work factor. The password is neither stored nor logged. Authentication failure returns one generic decryption error. Encryption metadata is authenticated and designed for future parameter upgrades. Encryption does not establish who created the snapshot.

Portable export treats removable drives and synchronized folders as failure-prone destinations. Replica rejects reparse points, unsafe leaf names, insufficient space, FAT32 single-file overflow, and existing final names. It copies to a unique sibling temporary extension, flushes it, compares source and destination SHA-256 and size, and only then assigns the final `.replica` name. Synchronization completion remains a user-visible check because a local hash cannot prove that a cloud provider uploaded the bytes.

Offline Recovery Pack installers are never trusted merely because they are inside a Snapshot. Authoring requires Windows-trusted Authenticode, records the publisher certificate SHA-256 plus exact file hash and size, and refuses unsigned/untrusted or changed files. Post-reset export verifies the stored Snapshot identity and whole-archive digest, extracts only declared entries, repeats hash/size/certificate checks both before and after no-overwrite publication, removes a mismatched final file, and never runs an installer. Provenance and licensing warnings remain user-reviewed claims, not cryptographic proof of redistribution rights.

## Restore execution

All restores begin as a typed plan. There is no general `ShellCommand` action. Handlers construct explicit executable paths and argument lists for allow-listed tools, validate package identifiers and destination paths, enforce timeouts/cancellation/output limits, and verify outcomes with a rescan.

The normal UI is unelevated. Administrator work uses the same identified `Replica.exe` in a separate elevated executor mode. Its plan file has restrictive ACLs, a SHA-256 digest, short expiry, one-use random token, session binding, and only administrator-required machine-environment, PATH, or allow-listed registry actions. Package installation always starts unelevated. The elevated process validates all fields independently, displays the exact operation, source key, and target value for a second elevated-side confirmation, deletes the plan after use, and does not host the normal UI.

Environment and PATH edits are structured merges, never whole-block replacement. Registry writes are limited to plugin-approved keys and value types. File restore rejects destinations outside the approved root, reparse points, oversized payloads, and checksum mismatches; it backs up existing content and performs atomic replacement.

## Rollback and retention

Before a supported mutation, Replica writes the original state to `%LOCALAPPDATA%\Replica\Rollback\<SessionId>`, flushes it, and updates checksums. A journal failure blocks the mutation. Journal access is restricted to the user and elevated executor when needed. Retention is bounded and deletion is user-visible.

Cancellation is honored before a mutation. Once an ordinary or elevated action starts, Replica waits through verification and the non-cancellable durable journal commit before observing cancellation or beginning another action; the parent process never kills an elevated child across that boundary.

Application installation is not automatically reversed by uninstalling it. The journal documents manual follow-up. Rollback never expands beyond changes recorded for that Replica session.

## Release and update security

Official downloads come from repository releases, use HTTPS, and are matched by expected owner/repository and asset name. Replica verifies SHA-256 and shows platform signing status. Stable automation requires Windows Authenticode plus Apple Developer ID notarization; allowed prereleases without protected identities are labeled Unsigned or ad-hoc signed/not notarized.

The optional before-reset installer copy accepts only `ReplicaSetup.exe` from published `HechoLP/Replica` GitHub Releases after explicit approval. It is stored beside recovery material rather than inside a Snapshot, streams to a temporary name with bounded declared size, verifies GitHub's SHA-256 digest when present, and is never launched automatically. A missing release digest is shown as unavailable rather than treated as verification.

The in-app update workflow additionally separates lookup, download, and installer launch. Channel filtering and strict SemVer parsing prevent prerelease drift. Downloads use randomized application-owned Temp sessions, bounded response and Asset sizes, explicit timeout/cancellation, official URL and filename allow-lists, and SHA-256 agreement across available sources. Launch requires a second approval and repeats path, size, reparse-point, hash, certificate-chain, and pinned-publisher validation while the file remains locked. Signed release builds embed the exact signing-certificate SHA-256; builds without an explicit policy fail closed. Replica passes no silent or arbitrary arguments and does not force elevation.

CI uses least-privilege tokens, pinned actions, protected secrets, a protected default-branch manual workflow, runtime validation against the repository's actual default branch, and immutable version tags. Restore, build, tests, audit, package-tool installation, and self-contained publish run on credential-free jobs. Prepared application bytes and the Inno tool tree cross into fresh signing runners only through short-lived artifacts and are revalidated against exact manifests, public tree digests, and publisher-certificate pins before credentials exist. Signing identities are exposed only to the bounded package/sign/notarize steps, raw credential files are deleted immediately after import, and cleanup fails unless identity absence is proven before artifact upload. Stable publication fails when either platform identity or required public pins are unavailable or mismatched. Repository settings must add required reviewers and protected-ref rules to `release-signing` and `release-publishing`, protect the current default branch and `v*` tags, and maintain the reviewed Inno tree and publisher pins.

## Vulnerability reporting

Do not place exploitable details or real sensitive samples in public issues. Until a private reporting channel is configured, contact the repository owner through a private GitHub profile channel and provide only the minimum reproduction data. The project should enable GitHub private vulnerability reporting before its first public binary release.

The executable regression matrix and local verification commands are documented in [SECURITY_TESTING.md](SECURITY_TESTING.md).
