## Summary

Describe the problem and the focused change that solves it.

## Change type

- [ ] Feature
- [ ] Fix
- [ ] Documentation
- [ ] Tests or hardening
- [ ] Build, CI, or maintenance

## Safety and privacy

- [ ] Read-only discovery remains separate from comparison, planning, execution, verification, and rollback.
- [ ] Every system change is typed, allow-listed, shown in a Dry Run, explicitly approved, and journaled before mutation.
- [ ] Tests use fakes or temporary data and do not alter the real Windows environment.
- [ ] No secrets, credentials, tokens, private keys, user files, Snapshots, logs, databases, installers, or generated recovery data are included.
- [ ] Path, archive, cancellation, timeout, partial-failure, elevation, and rollback boundaries affected by this change were reviewed.
- [ ] Not applicable; this change cannot affect a system or sensitive data.

## Verification

List the exact commands run and summarize the results.

```text
dotnet restore Replica.sln
dotnet format Replica.sln --verify-no-changes --no-restore
dotnet build Replica.sln -c Release --no-restore
dotnet test Replica.sln -c Release --no-build
```

## User experience and documentation

- [ ] User-visible behavior and limitations are documented accurately.
- [ ] Accessibility, localization, cancellation, progress, and error handling were considered.
- [ ] Screenshots and fixtures contain synthetic data only.
- [ ] Not applicable; there is no user-visible behavior.

## Checklist

- [ ] The branch is based on the latest default branch and the change is focused.
- [ ] Nullable analysis, warnings-as-errors, formatting, and tests pass.
- [ ] New behavior includes success, failure, and security-boundary tests where applicable.
- [ ] I reviewed both the working-tree diff and the staged diff.
