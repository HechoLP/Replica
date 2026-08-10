# Security and performance verification

Replica treats Snapshot, recovery, elevated-plan, plugin, database, process, and GitHub Release data as untrusted. Automated verification uses only temporary directories, deterministic fixtures, bounded streams, and fake process or HTTP adapters. It never installs software or writes the real registry, environment, PATH, fonts, or user files.

## Attack regression matrix

| Boundary | Regression coverage |
| --- | --- |
| `.replica` archive | deterministic fuzz corpus, corrupt ZIP, ZIP Slip and absolute paths, reserved paths, duplicate/case-colliding entries, link/reparse metadata, entry count, total size, compression ratio, JSON size/depth, unsupported schema, checksum mismatch and duplicate checksum records |
| Snapshot privacy | sensitive environment names are excluded, passwords are redacted, wrong-password errors are generic, plugin artifacts use bounded relative paths and content |
| Elevated executor | digest tampering, expiry, strict typed arguments, single-use replay, concurrent consumption, administrator-only action allow-list |
| Restore execution | package identifiers are allow-listed, Low/Unknown confidence is blocked, cancellation/timeout are propagated, journal failure prevents mutation, dependency failures skip downstream actions |
| Recovery and rollback | invalid mappings, path containment, corrupt state, interrupted-session resume, partial rollback failure, corrupt journal, checksum mismatch, duplicate rollback |
| SQLite history | corrupt databases fail closed without replacement; migrations and indexed hashes remain verified |
| Updates | official HTTPS repository paths, response size/depth, Asset filename and count, redirect host, download size, cancellation/timeout, checksum mismatch, launch-time revalidation and a read lock across hash-to-execute |
| Logging and telemetry | global exception logs omit exception messages, values, and sources; no telemetry subsystem is registered or shipped |

## Performance regression fixtures

- 2,000 stable application identities are matched deterministically.
- 20,000 file-manifest comparison items are processed without producing spurious differences.
- 5,000 extension artifacts are validated with bounded paths and content.
- A 32 MiB Recovery payload is hashed, archived, reopened, and verified through streaming I/O, with incomplete temporary data absent afterward.
- A pending scanner task leaves the WPF command responsive and cancellable.

Timing assertions are deliberately broad to detect algorithmic or blocking regressions without turning normal hosted-runner variation into flaky failures. Resource safety is primarily enforced by explicit count, byte, depth, compression-ratio, timeout, cancellation, and temporary-file limits.

## Local verification

```powershell
dotnet restore Replica.sln
dotnet list Replica.sln package --vulnerable --include-transitive
dotnet format Replica.sln --verify-no-changes --no-restore
dotnet build Replica.sln -c Release --no-restore
dotnet test Replica.sln -c Release --no-build
```

The build enables the .NET SDK analyzers, nullable reference analysis, code-style enforcement, deterministic output, and warnings as errors. GitHub Actions runs the same checks with minimum read permissions. A separate CodeQL workflow analyzes C# with the `security-extended` query suite and grants `security-events: write` only to the analysis job. Public repositories run it automatically. A private repository runs it only after GitHub Code Security is available and the repository variable `CODEQL_PRIVATE_ENABLED` is set to `true`; otherwise the job is intentionally skipped instead of failing for licensing reasons.
