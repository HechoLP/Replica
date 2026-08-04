# AGENTS.md

These instructions apply to the entire repository.

## Product invariants

- Replica is one user-facing Windows application. Do not introduce a separately installed helper, service, runtime, plugin manager, or database product.
- The supported client is Windows 11. The planned implementation stack is C#, .NET 10 LTS, WPF, MVVM, SQLite, `System.Text.Json`, ZIP-based `.replica` archives, winget, Inno Setup, and xUnit.
- GitHub Releases is the only official distribution channel. The user downloads one artifact: `ReplicaSetup.exe`.
- Every system-changing operation must support a dry run and require review of a typed restore plan before execution.
- Prefer mocks and fakes in automated tests. Tests must never install software or alter the real registry, environment, PATH, fonts, or user files.
- Use least privilege. Keep the normal UI unelevated and elevate the same executable only for the smallest approved set of administrator actions.

## Safety and privacy

- Never execute arbitrary shell command strings. Model each supported action as a typed, validated operation with allow-listed arguments.
- External processes must have timeouts, cancellation support, captured exit codes, and bounded stdout/stderr handling. Never block the UI thread.
- Keep business logic out of code-behind. Views bind to view models; domain and orchestration logic belongs in Core or Infrastructure.
- Never log secrets, credentials, tokens, cookies, private keys, recovery keys, payment data, or file contents. Redact sensitive names and values.
- Do not collect files that the user did not explicitly select. Broad locations such as all of AppData, Windows, Program Files, or ProgramData are not valid implicit selections.
- Do not commit snapshots, selected user files, installers, databases, logs, rollback journals, or generated recovery data.
- Never automatically remove extra applications or perform automatic downgrades. High-risk operations remain explicit manual actions.
- Before every mutation, persist the rollback journal entry. If journaling fails, do not perform the mutation.
- File and archive code must defend against path traversal, ZIP Slip, absolute paths, reparse points, duplicate entries, decompression bombs, and unsafe overwrite.

## Engineering rules

- Keep nullable reference types enabled, warnings as errors, deterministic builds, central package management, and formatting checks.
- Pass `CancellationToken` through asynchronous boundaries and report progress for long-running work.
- Use atomic writes where practical: create a sibling temporary file, flush and validate it, then replace or move it into place without silently overwriting an existing snapshot.
- Separate discovery, comparison, planning, execution, verification, and rollback. Discovery and diffing must not mutate the machine.
- Treat partial failure as a normal, reportable result. Preserve enough evidence for retry or rollback without exposing sensitive data.
- Add tests for security boundaries and failure paths alongside feature tests.

## Git and release rules

- Work on a focused branch named `feat/*`, `fix/*`, `docs/*`, or `chore/*`; do not commit directly to the default branch.
- Stage only files relevant to the current task. Review `git diff` and `git diff --cached` before committing.
- Do not rewrite published history, force-push, use destructive resets, or delete unrelated files.
- Use a pull request and required CI checks. Prefer squash merge and delete the remote feature branch after merge.
- Releases are tag-driven. CI must pass before creating release artifacts.
- Never commit private snapshots, user data, credentials, signing keys, or generated release secrets.
