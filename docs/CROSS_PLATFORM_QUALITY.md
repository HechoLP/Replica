# Cross-platform quality status

This document records the focused Windows/macOS audit and the boundaries that remain after the current hardening work. It is a status record, not a claim of feature parity.

## Completed hardening stages

1. **Shared privacy and archive identity:** Windows scanning, macOS scanning, and the Snapshot writer use one sensitive-name policy. The macOS Preview keeps environment values only for a narrow allow-list. Snapshot schema `1.1` requires mutually consistent platform metadata, while legacy `1.0` uses a constrained compatibility path.
2. **macOS Preview reliability:** application discovery covers bounded nested and system bundles, handles binary property lists through a fixed typed system operation, identifies thin Mach-O architecture, reports partial providers in Snapshot exclusions, opens associated Finder documents through validation, defaults updates to Stable, and rejects malformed or non-official Release metadata.
3. **Windows comparison and review:** Snapshot opening now performs archive validation, a fresh read-only scan, deterministic Diff display, checkbox-scoped planning, and mode-based typed plan regeneration. Recovery approval displays each action and no longer proposes an untrusted source path as its destination.
4. **Built-in plugin capture:** installed plugins are detected and captured, rather than writing catalog identifiers with empty payloads. Per-plugin failures remain bounded partial failures and do not hide the rest of the scan.
5. **Packaging contracts:** macOS packaging validates exact architecture, executable permissions, versioned property-list values, code-signature integrity, DMG integrity, read-only mount contents, and SHA-256. Tagged stable releases require Developer ID signing, hardened runtime, notarization, and ticket stapling; allowed prereleases may remain explicitly ad-hoc signed. Pull-request CI covers both ARM64 and x64 artifacts.
6. **Fail-closed restore review:** an incomplete Windows inventory blocks planning, archive input cannot reintroduce sensitive environment values, and opening or changing a comparison invalidates every earlier approval.
7. **Offline installer trust:** Windows Offline Recovery Pack authoring requires a reviewed trusted Authenticode publisher and binds the certificate, SHA-256, and size. Post-reset export revalidates the whole pack and each installer, never overwrites, and never executes payloads.
8. **Stable release trust gates:** tagged Stable automation refuses publication unless the Windows package is Authenticode signed and both Mac DMGs are Developer ID signed and notarized. Protected identities are removed from the runner after packaging.

## Deliberate platform boundary

Windows 11 x64 remains the migration and recovery client. macOS 13+ remains read-only Preview scope: scanning, Lightweight Snapshot creation/validation, Snapshot inspection, and official Release lookup. macOS does not register typed restore handlers, elevation, journaling, rollback, or update installation. Adding those capabilities requires a separate platform-specific threat review and implementation.

## Remaining gaps

- macOS font family/style metadata is still best-effort and may fall back to file-derived names. Commercial font files are never copied automatically.
- macOS x64 is cross-packaged and structurally inspected in CI, but an interactive launch on representative Intel hardware remains a release checklist item.
- Production signing identities and a completed live signed/notarized workflow run still depend on repository-owner credentials and Apple/Microsoft trust services. Unsigned/ad-hoc output remains restricted to visibly marked prereleases.
- Windows rollback needs finer per-item selection and a narrow elevated path for administrator-only rollback subsets.
- Unsupported built-in plugin restore types remain typed manual actions until an allow-listed handler and complete payload mapping exist. They are not sent to a generic shell or falsely marked executable.
- Clean-machine, human-driven Windows recovery/rollback and both-platform accessibility walkthroughs remain release gates beyond automated tests.
- The test suite still uses the legacy xUnit v2 runner. Moving to xUnit v3 requires a focused migration of cancellation-token usage and self-contained executable test references; this is a tooling maintenance item, not a known runtime vulnerability.

These gaps block a Stable-quality claim. They do not weaken the rule that no machine mutation occurs before a reviewed typed plan and final approval.
