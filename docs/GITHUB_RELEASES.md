# GitHub Releases

## Official channel

Official Replica builds are published only from `HechoLP/Replica` GitHub Releases. The single user-facing download is `ReplicaSetup.exe`; SHA-256 checksum material accompanies it. Internal DLLs and the self-contained .NET runtime may be inside the installed product but are not separate prerequisites.

## Version and channel policy

Tags use SemVer, beginning with forms such as `v0.1.0-alpha.1`. Tags containing alpha, beta, or RC identifiers publish GitHub pre-releases. Stable SemVer tags publish normal releases.

The release workflow is tag-driven and must:

1. verify tag/version consistency and a clean source revision;
2. restore with a locked dependency graph where supported;
3. run formatting checks, Release build, and all automated tests on `windows-latest` with .NET 10;
4. publish self-contained `win-x64` application output;
5. build `ReplicaSetup.exe` with Inno Setup;
6. verify installer presence and basic install/package assertions;
7. compute SHA-256 checksums;
8. generate release notes from reviewed repository history;
9. publish artifacts only after every required job succeeds.

Workflow permissions default to read-only and grant `contents: write` only to the release publishing job. Third-party actions are pinned to immutable revisions where practical. Release creation must be idempotent or fail safely without replacing an existing asset silently.

## Update checks

Replica queries the GitHub Releases API for the configured owner `HechoLP` and repository `Replica`. It compares stable users only with stable releases unless the user explicitly opts into a pre-release channel.

The application shows version, channel, publication date, release notes link, asset name, size, checksum availability, and signing status. Download is opt-in. Replica verifies the downloaded SHA-256 value and, when signing is available, the Authenticode signature before offering installation. A missing code signature is displayed plainly as **Unsigned**; it is never represented as verified.

Update failure does not block snapshot or offline recovery features. API responses, redirects, file names, sizes, and hashes are treated as untrusted input and bounded appropriately.

## Retention and provenance

Release notes link to the source tag and commit. Checksums are generated in CI from the exact uploaded installer. Build logs must not contain signing keys or secrets. Signing credentials, when introduced, live only in protected CI secret storage with environment approval and rotation procedures.
