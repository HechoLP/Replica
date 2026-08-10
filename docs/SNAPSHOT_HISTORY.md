# Snapshot history and comparison

Replica keeps a local SQLite index at `%LOCALAPPDATA%\Replica\replica.db` so users can find and compare previously created snapshots. The `.replica` file remains the portable source of truth. Snapshot ZIP bodies, selected user-file contents, passwords, and credentials are never copied into SQLite.

## Stored metadata

The history database stores the snapshot identifier, display name, description, tags, file path, whole-file SHA-256, creation time, snapshot type, source machine, file size, encryption flag, optional environment score, and whether the external file still exists. It also stores restore and rollback result summaries and explicit package-matching overrides.

For comparison, Replica indexes only structured identifiers and values plus file metadata:

- application/package identity and version;
- supported built-in plugin setting keys and sanitized values;
- artifact archive path, size, last-write time, and SHA-256.

The database uses ordered, transactional schema migrations. Foreign keys cascade comparison indexes when a history record is removed, while restore summaries retain their audit value without requiring the snapshot row to remain.

## History operations

The WPF history view lists name, description, tags, creation time, type, size, source PC, encryption, environment score, path, and current file availability. Name, description, and tags are editable.

Deletion always requires a separate confirmation. The user chooses between removing only the local history record and deleting both the record and the exact `.replica` file. Missing files can still have their stale history entry removed.

## Snapshot comparison

Comparison reads the SQLite metadata index rather than reopening or extracting user files. Items are classified as added, changed, or removed across applications, supported plugin settings, and explicitly selected user files. File changes use size, recorded last-write time, and SHA-256; the UI also shows size deltas and hash changes.

This supports Lightweight and Recovery snapshots. Existing snapshots created before last-write metadata was introduced remain comparable by size and hash.

## Restoring a past state

Selecting an earlier snapshot can create a comparison against a fresh current-PC scan. The result is a typed `RestorePlan` in `PendingReview` state and is shown in the existing Dry Run UI. It is never approved or executed automatically, never removes Extra applications, and never overwrites the whole environment. Selected user files require explicit path mappings through the recovery workflow.

## Security

Snapshot indexing first performs the normal hostile-archive validation and computes the whole-file SHA-256 with bounded streaming. Encryption passwords remain caller-owned memory and are cleared by the UI after use. SQLite paths reject reparse points, SQL uses parameters, and tests operate only on temporary databases and fake restore runtimes.
