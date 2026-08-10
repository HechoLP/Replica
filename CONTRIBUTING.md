# Contributing to Replica

Thank you for considering an improvement to Replica. Safety, privacy, and recoverability take priority over feature breadth because Replica can prepare and apply changes to a Windows environment.

## Before contributing

Replica does not currently have a selected license. External reuse and redistribution are not generally licensed, and substantial outside contributions should not begin until the owner has agreed on contribution and project licensing. Start with a small issue or discussion proposal before investing significant work.

Submitting an issue or pull request does not grant permission to reuse the repository and does not by itself change the copyright status described in [License decision](docs/LICENSE_DECISION.md).

Read [AGENTS.md](AGENTS.md), the [architecture](docs/ARCHITECTURE.md), [security model](docs/SECURITY.md), and [Git workflow](docs/GIT_WORKFLOW.md) before making changes.

## Development setup

Use Windows 11 and the .NET SDK selected by [global.json](global.json). Installer work also requires Inno Setup 6.

```powershell
dotnet restore Replica.sln
dotnet list Replica.sln package --vulnerable --include-transitive
dotnet format Replica.sln --verify-no-changes --no-restore
dotnet build Replica.sln -c Release --no-restore
dotnet test Replica.sln -c Release --no-build
```

Automated tests must use fakes, fixtures, and temporary directories. They must never install or uninstall software, change the real registry, environment, PATH, fonts, or user files, launch an installer, or use live credentials.

## Making a change

1. Open or reference an issue that explains the problem and its safety boundary.
2. Branch from the latest default branch using a focused `feat/*`, `fix/*`, `docs/*`, or `chore/*` name.
3. Keep discovery, comparison, planning, execution, verification, and rollback separate.
4. Add tests for normal behavior, cancellation, partial failure, and relevant security boundaries.
5. Update user-facing and architectural documentation when behavior changes.
6. Run all local verification commands and review both the working-tree and staged diff.
7. Open a pull request and wait for required CI checks. Do not force-push published history.

## Safety requirements

- Represent system changes as typed, allow-listed operations. Never accept arbitrary shell command strings.
- Require a reviewed Dry Run and final approval before mutation.
- Write and verify the rollback journal before every supported mutation; if journaling fails, stop.
- Keep the main UI unelevated and elevate the same executable only for the minimum approved administrator actions.
- Pass `CancellationToken`, enforce timeouts, bound process output, and keep long work off the UI thread.
- Do not collect unselected user files or log credentials, tokens, cookies, private keys, Snapshot passwords, environment values, or file contents.
- Reject path traversal, ZIP Slip, absolute archive paths, reparse points, duplicates, unsafe overwrite, and decompression-limit violations.
- Do not automatically remove extra applications, downgrade software, reboot Windows, or uninstall programs during rollback.

## Pull requests

Keep a pull request focused and complete its template. Include the verification commands run, test evidence, user-visible changes, risk and rollback impact, and documentation updates. Screenshots should contain synthetic data only.

Reviews may ask for smaller scope, additional failure tests, or a design document before accepting code that changes the Snapshot format, elevated executor, restore semantics, rollback contract, installer, or release pipeline.

## Conduct and security

Participation is governed by [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md). Report security problems privately according to [SECURITY.md](SECURITY.md); never include a vulnerability, malicious archive, secret, or private user data in a public issue.
