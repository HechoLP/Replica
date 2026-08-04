# Release process

## Versioning

Replica uses SemVer tags prefixed with `v`. Early examples are `v0.1.0-alpha.1`, `v0.1.0-beta.1`, and `v0.1.0-rc.1`. Alpha, beta, and RC are GitHub pre-releases; versions without a prerelease component are normal releases.

Application, installer, manifest, and tag versions must agree. Release tags point to reviewed commits on `main` and are not moved.

## Pre-release checklist

- Choose the intended channel and update product/release notes through a PR.
- Confirm `main` is clean, protected, and fully synchronized.
- Confirm formatting, Release build, unit/integration/security tests, and packaging checks pass on Windows.
- Review dependency changes, third-party notices, installer contents, permissions, update endpoint, and rollback compatibility.
- Confirm snapshot schema compatibility and document migrations or breaking changes.
- Confirm no credentials, signing material, snapshots, user data, logs, databases, or local paths entered the source or artifacts.
- Confirm code-signing availability. If unavailable for an allowed prerelease, mark the installer and notes **Unsigned**.

## Automated release

Create and push the approved annotated version tag. The tag workflow restores and tests from scratch, publishes self-contained `win-x64` output, creates `ReplicaSetup.exe` with Inno Setup, validates packaging, applies Authenticode when configured, calculates SHA-256, and prepares release notes.

Only after all jobs succeed does the workflow create the GitHub Release and upload the installer and checksum material. It marks the release as prerelease based on the version. A failed workflow leaves no official partial release; maintainers investigate and use a new version if an immutable published tag or asset was exposed.

## Verification after publication

1. Download from the public release page, not the workflow workspace.
2. Verify filename, size, published SHA-256, and Authenticode status.
3. Test installation, launch, displayed version/channel, update-check behavior, and uninstall in a clean supported Windows VM.
4. Run a non-destructive scan and open a test snapshot.
5. Confirm release notes and source/tag links.
6. Record the result. If validation fails, remove or clearly mark the release unavailable, disclose impact, and issue a new version after correction; do not silently replace trusted bytes.

## Rollback and incident handling

Desktop releases are immutable and there is no forced client downgrade. For a severe defect, mark the affected release, publish guidance, and ship a corrected higher version. For signing-key or CI compromise, stop publishing, revoke/rotate credentials, preserve evidence, assess all affected releases, and communicate verified hashes and remediation.

## Stable release gate

A stable release requires successful clean-VM recovery exercises across supported Windows 11 builds, rollback failure-path testing, accessibility and privacy review, update and installer verification, parser security testing, documented known limitations, and a deliberate license decision.
