# ADR 0005: GitHub Releases as the distribution channel

- Status: Accepted
- Date: 2026-08-04

## Context

Replica needs a simple clean-install acquisition path, versioned artifacts, pre-release channels, release notes, checksums, and an update metadata source. Operating a separate website, update server, and artifact store would increase attack surface and maintenance before the product is established.

## Decision

Use `HechoLP/Replica` GitHub Releases as the sole official distribution and update metadata channel. A protected default-branch, manually dispatched GitHub Actions workflow accepts an existing annotated tag reachable from `main`, builds and tests on Windows, packages the self-contained application with Inno Setup, and publishes one user-facing asset named `ReplicaSetup.exe` plus SHA-256 checksum material.

SemVer prerelease tags map to GitHub pre-releases. Stable tags map to normal releases. In-app update checks use the GitHub Releases API, downloads require user consent, and checksum/signature status is displayed. Unsigned releases are labeled Unsigned.

## Consequences

Source, reviews, CI, artifacts, and update metadata are traceable in one place and the user has one download. Availability and rate limits depend on GitHub. Release workflow permissions and third-party actions are supply-chain boundaries. A future alternate channel, CDN, store package, or mandatory updater requires a new ADR and migration/security plan.
