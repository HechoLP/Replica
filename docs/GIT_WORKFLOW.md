# Git workflow

## Branches

Work begins from the repository's current protected default branch and uses a focused topic name. Do not assume the default branch is named `main`; use the branch displayed by GitHub for this repository.

- `feat/<topic>` for product behavior;
- `fix/<topic>` for defects;
- `docs/<topic>` for documentation only;
- `chore/<topic>` for tooling and repository maintenance.

Do not commit directly to the protected default branch or rewrite published history. Avoid long-lived integration branches.

## Local workflow

1. `git switch <default-branch>`
2. `git pull --ff-only`
3. `git switch -c <type>/<topic>`
4. Make one coherent change and run relevant verification.
5. Inspect `git status` and `git diff`.
6. Search the diff and staged files for credentials, snapshots, personal files, installers, logs, databases, and generated recovery data.
7. Stage only intended paths; do not use blanket staging when unrelated changes exist.
8. Inspect `git diff --cached`.
9. Commit with a concise imperative Conventional Commit message, for example `docs: define Replica recovery and release architecture`.
10. Push with upstream tracking and open a pull request.

Never use force push, destructive resets, or broad cleaning as part of the normal workflow. Preserve unrelated user changes.

## Pull requests

The PR explains the user-facing outcome, architecture/security implications, tests run, screenshots for UI changes, migration/rollback behavior, and remaining limitations. Keep it small enough to review. Draft PRs are welcome for early CI but are not merged.

Required review attention includes archive boundaries, elevation, external processes, system writes, selected files, sensitive-data handling, package/update identity, and release permissions.

Required checks are formatting, Release build, tests, and any task-specific security or packaging checks. Resolve actionable review discussions and rerun affected tests. Merge only when approvals and required checks pass.

## Merge and cleanup

Use squash merge so the default branch has one intentional commit per PR. The squash title follows the repository's commit convention. Delete the remote topic branch, switch to the current default branch, and update with `git pull --ff-only`. Tags are created only by the release process, never on a topic branch.
