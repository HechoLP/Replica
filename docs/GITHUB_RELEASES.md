# GitHub Releases

## Official channel

Official Replica builds are published only from `HechoLP/Replica` GitHub Releases. Users download one package for their platform: `ReplicaSetup.exe`, `Replica-macOS-arm64.dmg`, or `Replica-macOS-x64.dmg`. SHA-256 material accompanies each package. Internal DLLs and the self-contained .NET runtime are not separate prerequisites.

## Version and channel policy

Tags use SemVer, beginning with forms such as `v0.1.0-alpha.1`. Tags containing alpha, beta, or RC identifiers publish GitHub pre-releases. Stable SemVer tags publish normal releases.

The release workflow is tag-driven and must:

1. verify tag/version consistency and a clean source revision;
2. restore with a locked dependency graph where supported;
3. run Windows and macOS formatting, Release builds, and automated tests with .NET 10;
4. publish self-contained `win-x64`, `osx-arm64`, and `osx-x64` application output;
5. build `ReplicaSetup.exe` with Inno Setup and architecture-specific macOS DMGs;
6. verify package presence, bundle metadata, executable permissions, and package assertions;
7. compute SHA-256 checksums for every package;
8. generate release notes from reviewed repository history;
9. publish all platform Assets only after every required job succeeds.

Workflow permissions default to read-only and grant `contents: write` only to the release publishing job. Third-party actions are pinned to immutable revisions where practical. Release creation must be idempotent or fail safely without replacing an existing asset silently.

The implementation lives in `.github/workflows/release.yml`. A tag push or manual dispatch accepts only an existing annotated tag matching `v<major>.<minor>.<patch>` with an optional `alpha.N`, `beta.N`, or `rc.N` suffix. The tag must point at the checked-out commit and its version must exactly match `VersionPrefix` plus `VersionSuffix` in `Directory.Build.props`; no workflow creates a tag.

The Windows packaging job retains `ReplicaSetup-<Version>.exe` in its short-lived Actions Artifact for traceability and publishes the same verified bytes as `ReplicaSetup.exe`. macOS jobs independently construct and verify ARM64 and x64 DMGs. Alpha, beta, and RC tags become pre-releases. Stable tags become normal releases. Notes mark Windows as **Unsigned** until Authenticode is configured and macOS as ad-hoc signed and **not notarized** until Apple Developer ID signing is configured.

Installer provenance attestation runs when the repository visibility and GitHub plan support public Artifact Attestations. Attestation has only `id-token: write` and `attestations: write`; the separate publication job alone receives `contents: write`. A failed validation, build, test, dependency audit, package check, hash check, or supported attestation blocks publication.

## Update checks

Replica queries the GitHub Releases API for the configured owner `HechoLP` and repository `Replica`. Stable is the default. Beta permits beta and RC releases but excludes alpha; Alpha permits every supported prerelease. A selected channel never receives a more unstable automatic suggestion. Strict semantic-version parsing prevents an unknown tag format from becoming an update.

The application shows version, channel, publication date, release notes link, asset name, size, checksum availability, and signing status. Download is opt-in. Replica verifies the downloaded SHA-256 value and, when signing is available, the Authenticode signature before offering installation. A missing code signature is displayed plainly as **Unsigned**; it is never represented as verified.

Update failure does not block snapshot or offline recovery features. API responses, redirects, file names, sizes, and hashes are treated as untrusted input and bounded appropriately. Draft releases are filtered, and offline, timeout, invalid JSON, missing Asset, and GitHub Rate Limit responses remain distinct user-visible states. `Retry-After` or `X-RateLimit-Reset` determines when a rate-limited check may be retried.

Approved updates download to a random session beneath Replica's Temp directory, not a Snapshot or recovery folder. The installer and any checksum Asset must remain on the official HTTPS release path. Downloads are cancellable, time-limited, size-limited, progress-reporting, and incomplete sessions are deleted. GitHub's installer digest and separate checksum material must agree when both exist.

Installer launch is a second approval. Replica rechecks path, name, size, reparse state, and SHA-256, starts `ReplicaSetup.exe` without silent arguments or a forced elevation verb, and then closes. A failed verification never reaches the process launcher.

The before-reset workflow may also retain `ReplicaSetup.exe` in a user-selected Recovery folder. This is an explicit download, separate from the `.replica` archive. Replica accepts only a published release from the configured owner/repository and the exact installer asset name, checks the declared size and GitHub-provided SHA-256 digest when available, writes through a temporary extension, refuses overwrite, and never starts the installer automatically. Users may select the latest stable release or an exact published tag.

## Retention and provenance

Release notes link to the source tag and commit. Checksums are generated in CI from the exact uploaded installer. Build logs must not contain signing keys or secrets. Signing credentials, when introduced, live only in protected CI secret storage with environment approval and rotation procedures.
