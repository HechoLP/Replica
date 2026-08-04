# ADR 0004: Diff-first, plan-driven restoration

- Status: Accepted
- Date: 2026-08-04

## Context

Directly applying a snapshot cannot account safely for newer applications, partial scans, changed paths, incompatible settings, extra software, or user intent. A trustworthy recovery tool must make differences and consequences visible before it changes Windows.

## Decision

Restoration follows distinct stages: validate snapshot, scan target, match identities, compute typed differences, select restore mode/items, build a dependency DAG of typed actions, show a dry run, obtain approval, journal, execute, and verify.

Safe is the default. Recommended expands to compatible updates/settings/merges. Exact provides fuller visibility but does not authorize automatic removals, downgrades, or destructive high-risk actions. Low/Unknown application matches and incomparable versions are manual-only. There is no arbitrary shell-command action.

## Consequences

Users receive explainable choices, testing can isolate policies, and execution has a bounded contract. Recovery takes more steps and may leave differences unresolved when safe automation is impossible. Plan schemas become security-sensitive, versioned data that elevated execution must validate independently.
