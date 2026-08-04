# ADR 0003: Three explicit snapshot types

- Status: Accepted
- Date: 2026-08-04

## Context

Inventory-only portability, clean-install recovery with selected data, and fully offline recovery have different storage, privacy, licensing, and trust costs. One implicit "backup" mode would either surprise users by collecting too much or fail to explain why recovery material is absent.

## Decision

Expose three explicit types in the manifest and UI:

- Lightweight Snapshot for inventory and supported settings without installers or personal files;
- Recovery Snapshot for Lightweight content plus explicitly selected files/folders and recovery preferences;
- Offline Recovery Pack for Recovery content plus explicitly selected, provenance-recorded offline installation material.

All use a versioned ZIP-based `.replica` container with checksums, capabilities, and exclusions. Recovery is the clean-install-oriented choice; Offline is opt-in and never the default. Sensitive exclusions apply to every type.

## Consequences

Users can understand size and privacy tradeoffs before capture, and readers can enforce capability-specific validation. UI, tests, schema evolution, and documentation must cover three types. Moving content between types requires a new validated write rather than relabeling a manifest.
