# Threat model

## Scope and assets

This model covers the desktop application, `.replica` files, local database/log/temp/rollback data, external scanners and installers, elevation handoff, GitHub update metadata, release CI, and built-in plugins.

Assets include selected user files and settings, inventory privacy, snapshot encryption keys in memory, system integrity, restore-plan intent, rollback evidence, release artifacts, signing credentials, and user trust in displayed results.

## Trust boundaries

- untrusted snapshot or offline installer to the archive reader;
- target machine data and registry/process output to scanners;
- GitHub/network responses to the update service;
- normal user process to elevated executor;
- Core policy to platform-specific writers;
- third-party dependencies and CI actions to release artifacts;
- plugin-provided data to snapshot, diff, and restore pipelines.

## Threats and mitigations

| Threat | Example impact | Primary mitigations |
| --- | --- | --- |
| Malicious archive paths | overwrite files outside extraction root | canonical relative-path checks, root containment, reparse-point rejection, no direct extraction |
| ZIP bomb or huge inventory | memory/disk exhaustion | entry, size, ratio, depth, and total limits; streaming reads; cancellation |
| Tampered snapshot | restore attacker-controlled settings/files | SHA-256 validation, authenticated encryption, schema validation, explicit trust warning |
| Snapshot disclosure | exposure of private files or setup metadata | minimal collection, explicit selection, sensitive exclusions, optional authenticated encryption, restricted temp ACLs |
| Command/argument injection | arbitrary code under user/admin token | typed actions, allow-listed executables, argument-list APIs, strict identity validation, no shell strings |
| Elevation handoff tampering/replay | unauthorized admin mutations | ACL-restricted plan, digest, expiry, single-use token, session binding, independent validation and deletion |
| TOCTOU path replacement | write through a newly introduced junction | revalidate resolved path and reparse state immediately before atomic operation |
| Poisoned installer or update | arbitrary code execution | official repository pinning, HTTPS, checksum and signature display/verification, user consent, post-install rescan |
| Overbroad settings plugin | secret capture or destructive restore | built-in allow-list, documented paths, secret inspection, code review, typed handlers, plugin capability declarations |
| Sensitive logging | credential leakage in diagnostics | structured redacted logs, bounded process output, never log payloads/secrets/passwords |
| Restore interruption | inconsistent machine state | pre-mutation journal, dependency graph, atomic writes, verification, partial result and rollback |
| Malicious/compromised CI | backdoored release | least privilege, protected tags/environments, pinned actions, review, provenance and checksums |
| Application misidentification | wrong software install/update | ordered stable identities, confidence threshold, user confirmation, no auto action for Low/Unknown |

## Abuse cases explicitly rejected

- Using Replica as a credential or browser-session migration tool.
- Embedding an executable in a snapshot and launching it on open.
- Encoding arbitrary PowerShell or command prompt instructions in a restore plan.
- Treating Exact mode as blanket consent to uninstall, downgrade, or overwrite.
- Capturing a whole profile because the user selected a high-level directory without understanding its contents.
- Reusing an elevated plan or swapping it after user confirmation.

## Residual risk

SHA-256 proves integrity against the declared snapshot manifest but does not prove the snapshot author's identity. Authenticated encryption protects stored contents but cannot protect a compromised endpoint while data is open. winget and vendor installers remain external trust dependencies. Some applications cannot be reproduced safely because their settings are undocumented or tied to hardware, accounts, licenses, or newer Windows versions.

Replica must communicate these limits and use `Unsupported` or `ManualActionRequired` rather than guessing.

## Security verification plan

Tests include traversal and path canonicalization variants, absolute/UNC/device paths, duplicate and case-colliding entries, malformed schemas, checksum mismatch, decompression-limit boundaries, wrong passwords without detail leakage, plan tampering/expiry/replay, reparse-point races where testable, sensitive-name redaction, process timeout/cancellation, journal-before-write enforcement, and release asset/hash mismatch.

Before the first public alpha binary, perform dependency review and an independent design review of archive reading, encryption, elevation, and update code. Before stable release, add fuzzing for snapshot parsing and a signed-build incident/rotation procedure.
