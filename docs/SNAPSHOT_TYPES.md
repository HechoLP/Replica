# Snapshot types

All snapshot types use the `.replica` extension and share a versioned manifest, inventory, plugin index, exclusions, recovery metadata, and checksums. Schema `1.1` requires an explicit source platform that agrees with the machine metadata; a mismatch is rejected. Legacy schema `1.0` is accepted only through a constrained compatibility path and is treated as Windows unless it already carries consistent, explicit macOS metadata. The reader never infers stronger capabilities from archive contents alone, and a macOS Snapshot is never treated as permission to run Windows restore actions.

## Lightweight Snapshot

Intended for portable inventory, comparison, and online reconstruction. It includes:

- application names, versions, publishers, architecture, scope, install form, and package identities;
- winget and MSIX/Store identities;
- supported application settings represented as structured data;
- non-sensitive environment variables and ordered PATH entries;
- Windows and development-environment inventory;
- installed font inventory;
- built-in plugin records and compact supported configuration artifacts.

It does not include application installers or personal files. Font files are not automatically copied.

## Recovery Snapshot

Contains the complete Lightweight payload plus only the folders and files that the user explicitly selects, such as Desktop, Documents, project folders, game saves, or supported application settings. It also stores conflict preferences, restore priority, and clean-install recovery notes.

Selection is granular and reviewed before creation. Replica shows per-file and total size estimates and records exclusions. Selecting a parent folder does not waive sensitive-data rules.

## Offline Recovery Pack

Contains the complete Recovery payload and adds explicitly selected offline installers for applications that cannot conveniently be reacquired. Before selection, Windows must validate the file's Authenticode chain and bind its publisher name and publisher-certificate SHA-256. The pack records provenance, version, architecture, exact file SHA-256 and size, and licensing/redistribution warnings.

Offline packs can be very large and carry higher supply-chain and data-retention risk, so they are never the default. Replica does not redistribute installers through its own release and does not assume that an installer may legally be copied.

The Windows authoring UI requires provenance before file selection, rejects unsigned, untrusted, unverifiable, reparse-point, empty, and oversized files, and limits each pack to 100 installers. Changing the file list invalidates the prior size estimate and final approval. During archive creation, Replica rereads each payload and refuses the pack if the reviewed hash or size changed.

After reset, the recovery workspace lists every installer as manual work. Export requires a separate destination and approval. Replica verifies the stored snapshot identity and whole-file SHA-256, extracts only declared installer entries to unique temporary names, checks archive hash/size metadata, revalidates Authenticode trust and the pinned publisher certificate, and never overwrites an existing file. Only verified files are moved into the destination, partial output is cleaned on failure, and Replica never executes them.

## Default exclusions

No snapshot type includes passwords, browser cookies, login sessions, Windows Credential Manager data, Discord/Steam/GitHub/OAuth tokens, API keys, `.env` files, SSH private keys, BitLocker recovery keys, OBS stream keys, payment information, certificates with private keys, cloud-login state, cryptocurrency wallets, or recovery phrases.

Replica also refuses implicit collection of all AppData, Windows, Program Files, ProgramData, browser profiles, caches, logs, temporary data, or large game install directories. A supported plugin may select narrowly defined, documented setting files, subject to the same secret inspection and user review.

## Capability semantics

The manifest records what was captured and what can be restored. Missing capability means unsupported or not collected, not an empty value. Exclusions contain reason codes so the UI can distinguish user choice, sensitive data, unsupported content, size policy, read failure, and cancellation.

Optional encryption protects selected payloads at rest but does not make unsafe content acceptable to collect. See [SECURITY.md](SECURITY.md).
