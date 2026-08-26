# Windows installer

Replica is distributed as one user download, `ReplicaSetup.exe`, through the official `HechoLP/Replica` GitHub Releases page. The versioned local build output is `ReplicaSetup-<Version>.exe`; the reviewed release pipeline publishes those same bytes under the canonical download name.

## Build

Prerequisites are the .NET 10 SDK and Inno Setup 6. From the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-release.ps1 -Version 0.2.0-alpha.3
```

The script cleans only release directories below `artifacts`, restores, verifies formatting, builds, tests, publishes a self-contained single-file `win-x64` WPF executable, compiles the installer, validates its contract, and writes SHA-256 material. The only release outputs are:

```text
artifacts/release/
  ReplicaSetup-<Version>.exe
  ReplicaSetup-<Version>.exe.sha256
```

Pass `-RunInstallTests` only on a clean Windows test account or disposable VM. This opt-in smoke test installs Replica, launches the exact installed executable, verifies the `.replica` association, performs an in-place upgrade, uninstalls, and proves that user data remains. Automated xUnit tests never install software or change the registry.

When Windows Sandbox is enabled, the same smoke test can be run without changing the host installation:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-installer-sandbox.ps1 -Version 0.2.0-alpha.3
```

The Sandbox runner disables networking and clipboard redirection, maps the repository read-only, writes only generated evidence beneath `artifacts/sandbox-installer-test`, and shuts down the disposable Sandbox after the test. It validates installation, launch, `.replica` association, same-version upgrade, uninstall, displayed product version, and default user-data retention.

Snapshot creation/opening is covered by snapshot round-trip tests and the file-association argument/indexing tests. A release candidate must additionally complete the GUI checklist in a clean Windows 11 VM.

## Installation behavior

- Replica installs per user to `%LOCALAPPDATA%\Programs\Replica`; no administrator elevation is requested by setup.
- The installed product exposes only `Replica.exe`. No helper, service, runtime installer, or plugin manager is installed; built-in plugins are compiled into Replica.
- Setup creates a Start menu shortcut, offers an unchecked desktop shortcut, registers `.replica`, appears in Add/Remove Programs, supports in-place upgrades, and can launch Replica after setup.
- Silent and default interactive uninstall preserve `%LOCALAPPDATA%\Replica`, including snapshots, history, settings, and rollback records. Full data deletion is offered only during interactive uninstall and requires two explicit confirmations.

## Signing

An unsigned local or permitted prerelease build is plainly marked **Unsigned** in its pre-install notice and metadata. The build never creates a test certificate or presents an invalid signature as unsigned-but-acceptable. Verify the SHA-256 file before testing such a package.

For a production build, install a code-signing identity with an accessible private key in `Cert:\CurrentUser\My`, then pass its thumbprint. `build-release.ps1` derives the certificate's SHA-256, requires the update publisher allow-list to contain that exact certificate, signs `Replica.exe`, asks Inno Setup to sign setup and the generated uninstaller, timestamps every signature over HTTPS, and verifies the resulting signer before creating checksums:

```powershell
./scripts/build-release.ps1 `
  -Version 1.0.0 `
  -SigningCertificateThumbprint <thumbprint> `
  -PublisherCertificateSha256 <64-hex-certificate-sha256>
```

The tagged GitHub workflow accepts the protected `REPLICA_WINDOWS_SIGNING_PFX_BASE64` and `REPLICA_WINDOWS_SIGNING_PFX_PASSWORD` secrets, removes the imported identity and PFX in an unconditional cleanup step, and refuses a Stable release when they are absent. Certificate renewal or rotation requires a reviewed publisher-pin transition; never replace the pin without also reviewing update compatibility and incident response.

## Release verification checklist

In a clean supported Windows 11 VM, verify installation, launch, lightweight Snapshot creation, Snapshot opening from Explorer, upgrade, uninstall, `.replica` association, user-data retention, displayed version, installer SHA-256, and the expected Authenticode state. Do not publish if any check fails.
