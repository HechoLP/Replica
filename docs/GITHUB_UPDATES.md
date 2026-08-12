# GitHub Releases update service

Replica uses `HechoLP/Replica` GitHub Releases as its only update catalog and installer source. Update checks are read-only. Downloading and starting an installer are separate, explicitly approved operations.

The Windows client implements the full approved download and installer handoff described below. The macOS Preview performs catalog lookup only, selects `Replica-macOS-arm64.dmg` or `Replica-macOS-x64.dmg` for the running architecture, and opens the official Release page on request. It does not silently download, mount, or run a DMG.

## Channels and versions

The default channel is Stable:

- **Stable** accepts only `vMAJOR.MINOR.PATCH` releases;
- **Beta** accepts stable, `-rc.N`, and `-beta.N` releases, but never alpha releases;
- **Alpha** accepts every supported channel.

Replica parses the supported tags as strict semantic versions and orders alpha before beta, beta before RC, and RC before stable for the same core version. Unsupported or non-canonical tags are ignored rather than guessed. Draft releases are never offered. Selecting a channel persists per user, and skipping a version stores only its exact tag; a newer release is still offered.

## Release metadata

The bounded GitHub REST response supplies tag, release name, publication time, prerelease/draft flags, release notes, release page, and Asset metadata. Replica accepts only HTTPS pages and download URLs beneath the official `HechoLP/Replica` GitHub Release paths. It reports Rate Limit reset or retry time from GitHub headers and distinguishes offline, timeout, invalid JSON, unavailable service, missing installer, up-to-date, skipped, and update-available states.

## Download

The user must approve every download. Replica requires exactly one case-sensitive `ReplicaSetup.exe` Asset and validates its URL, filename, declared size, and maximum-size policy. It creates a random session beneath `%LOCALAPPDATA%\Replica\Temp\Updates`, streams to `.ReplicaSetup.download`, reports progress, supports cancellation, and enforces a ten-minute timeout.

SHA-256 verification uses, in order:

1. the installer Asset's GitHub `sha256:` digest;
2. an official `ReplicaSetup.exe.sha256` Asset;
3. an official `SHA256SUMS` entry for `ReplicaSetup.exe`.

If multiple sources exist they must agree. A malformed, oversized, missing-length, or mismatched checksum deletes the incomplete session and never produces an installable result. When no published checksum exists, Replica computes and displays its own SHA-256 and marks the checksum status unavailable rather than verified.

The completed installer is renamed only after its streamed and reopened hashes agree. It remains in the Replica Temp session and is never started by the download operation.

## Installation handoff

Before launch, Replica displays installer name, version, size, checksum state, and SHA-256, then asks again. The installer service verifies that the exact file remains beneath Replica's update Temp root, rejects reparse points, size changes, and hash changes, and launches only `ReplicaSetup.exe` with no arguments, no silent option, and no forced `runas` verb. Windows and the installer handle any later elevation request. After a successful interactive launch handoff, Replica closes its normal UI.

Tests replace GitHub HTTP, Asset streams, timeouts, process launch, and application lifetime with fakes. They never call the live GitHub API, start an installer, or alter Windows.

See GitHub's official documentation for the [Releases REST endpoint](https://docs.github.com/en/rest/releases/releases), [Release Asset metadata and download behavior](https://docs.github.com/en/rest/releases/assets), and [REST API Rate Limits](https://docs.github.com/en/rest/using-the-rest-api/rate-limits-for-the-rest-api).
