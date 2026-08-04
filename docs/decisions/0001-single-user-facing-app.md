# ADR 0001: One user-facing application

- Status: Accepted
- Date: 2026-08-04

## Context

Replica needs UI, scanning, snapshot, update, recovery, and occasional administrator capabilities. Splitting these into separately installed services, helpers, plugin managers, databases, or runtimes would complicate trust, upgrades, support, and the clean-install journey. The product promise is that a user downloads and installs one thing.

## Decision

Ship one user-facing product through one installer, `ReplicaSetup.exe`, with `Replica.exe` as the application entry point. Internal class libraries and a bundled self-contained runtime are allowed implementation details. Do not install a persistent service or separately managed helper.

When administrator rights are required, restart the same executable in a narrow elevated-executor mode for the administrator-only subset of an approved plan. Normal UI and read-only scanning remain unelevated.

## Consequences

Installation, update, removal, branding, and support remain coherent. The elevated mode requires a carefully authenticated short-lived plan and independent input validation. Long-running background recovery is limited to the lifetime and capabilities of the application rather than a permanent service. A future service would require a new ADR and threat model.
