# Release process

## Versioning

Replica uses SemVer tags prefixed with `v`. Early examples are `v0.1.0-alpha.1`, `v0.1.0-beta.1`, and `v0.1.0-rc.1`. Alpha, beta, and RC are GitHub pre-releases; versions without a prerelease component are normal releases.

Application, installer, manifest, and tag versions must agree. Release tags point to reviewed commits reachable from the repository's current protected default branch and are not moved.

## Pre-release checklist

- Choose the intended channel and update product/release notes through a PR.
- Confirm the current default branch is clean, protected, and fully synchronized.
- Confirm formatting, Release build, unit/integration/security tests, and packaging checks pass on Windows and macOS.
- Review dependency changes, third-party notices, installer contents, permissions, update endpoint, and rollback compatibility.
- Confirm snapshot schema compatibility and document migrations or breaking changes.
- Confirm no credentials, signing material, snapshots, user data, logs, databases, or local paths entered the source or artifacts.
- Confirm code-signing availability. If unavailable for an allowed prerelease, mark the installer and notes **Unsigned**.

## Automated release

Create and push the approved annotated version tag, then start the `Release` workflow manually from the protected default branch and select that existing tag. Tag pushes do not run secret-bearing release code. The workflow reads the current default branch from trusted repository metadata, fetches that exact branch, requires the tag commit to be reachable from it, and propagates the validated commit SHA to every downstream checkout so later tag movement cannot change signed bytes. It restores and tests from scratch. Windows publishes self-contained `win-x64` output and creates `ReplicaSetup.exe` with Inno Setup. macOS publishes self-contained `osx-arm64` and `osx-x64` Avalonia app bundles and creates architecture-specific DMGs. Every package is validated and receives SHA-256 material before release notes and Assets are published.

Manual dispatch never creates or moves a tag. Repository settings must protect the current default branch and `v*` tags, and the `release-signing` and `release-publishing` environments must require authorized reviewers and restrict deployment to protected refs. Credential-free jobs perform restore, build, test, audit, Inno installation, and self-contained publish, then end. Fresh Windows and macOS runners revalidate prepared-app manifests before importing identities. Windows additionally requires `REPLICA_INNO_SETUP_TOOL_TREE_SHA256` and `REPLICA_INNO_SETUP_PUBLISHER_CERTIFICATE_SHA256` to authenticate the exact reviewed compiler tree copied from the credential-free runner. Maintainers compute the former with `scripts/get-directory-tree-sha256.ps1 -RootPath '<reviewed Inno Setup 6 directory>'` and derive the latter from the verified `ISCC.exe` signer certificate; both changes require review. Cleanup must prove that certificates/keychains and raw files are absent before upload.

The local packaging entry point is `scripts/build-release.ps1`; detailed prerequisites, outputs, unsigned-package behavior, and clean-VM checks are documented in `WINDOWS_INSTALLER.md`.

Only after all jobs succeed does the workflow create the GitHub Release and upload the installer and checksum material. It marks the release as prerelease based on the version. A failed workflow leaves no official partial release; maintainers investigate and use a new version if an immutable published tag or asset was exposed.

## Verification after publication

1. Download from the public release page, not the workflow workspace.
2. Verify filename, size, published SHA-256, Windows Authenticode status, and macOS signing/notarization status.
3. Test Windows installation/uninstall in a clean supported VM and test both macOS bundles on representative Apple Silicon and Intel systems.
4. Run a non-destructive scan and open a test snapshot.
5. Confirm release notes and source/tag links.
6. Record the result. If validation fails, remove or clearly mark the release unavailable, disclose impact, and issue a new version after correction; do not silently replace trusted bytes.

## Rollback and incident handling

Desktop releases are immutable and there is no forced client downgrade. For a severe defect, mark the affected release, publish guidance, and ship a corrected higher version. For signing-key or CI compromise, stop publishing, revoke/rotate credentials, preserve evidence, assess all affected releases, and communicate verified hashes and remediation.

## Stable release gate

A stable release requires successful clean-VM recovery exercises across supported Windows 11 builds, rollback failure-path testing, accessibility and privacy review, update and installer verification, parser security testing, documented known limitations, a deliberate license decision, protected default-branch/`v*` rules, reviewed Inno tool pins, and required reviewers on both release environments.
