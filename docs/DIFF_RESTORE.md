# Diff restore

## Purpose

Diff restore compares a validated source snapshot with a fresh target scan. It explains what matches, what differs, what can be restored automatically, and what requires a user or unsupported tool. Computing a diff is always read-only.

## Difference types

- `ExactMatch`
- `Missing`
- `Extra`
- `VersionMismatch`
- `ValueMismatch`
- `FileChanged`
- `Conflict`
- `Unsupported`
- `SensitiveExcluded`
- `ManualActionRequired`
- `Error`

Each result identifies its category, source/current representation, matching confidence, restore support, risk, elevation, restart, and reason codes. Sensitive values are not included in display or logs.

## Application identity and versions

Applications match in this order: winget package ID, MSIX package family, MSI product code, publisher plus display name, normalized name plus install location, then explicitly labeled heuristic inference. Confidence is `Exact`, `High`, `Medium`, `Low`, or `Unknown`. Low and Unknown matches cannot produce automatic install/update actions.

Version comparison supports semantic, `System.Version`, date-based, prefixed, and pre-release forms, returning `Equal`, `SourceNewer`, `TargetNewer`, `Incomparable`, or `Unknown`. Incomparable versions never trigger automatic update or downgrade. Preview, beta, and RC channels remain distinct from stable.

## Comparison areas

Replica compares applications and versions, Store/MSIX packages, environment variables, ordered PATH, fonts, Windows compatibility data, supported plugin settings, configuration-file hashes, selected user files, and development environments. Unsupported or unscanned providers remain explicit rather than being treated as missing.

## Similarity score

The headline score is informational, not permission to restore. The initial weighting is:

- installed applications: 30%;
- development environment: 20%;
- application settings: 25%;
- environment variables and PATH: 15%;
- fonts and other supported inventory: 10%.

`Unsupported` and `SensitiveExcluded` are reported separately and removed from the relevant denominator. Categories with no comparable observations are shown as unavailable, not 100%. The UI displays both the overall score and category coverage so a partial scan cannot look complete.

## Planning policy

Safe mode selects supported Missing actions by default, preserves Extra items, blocks downgrades/removals, and prompts on file conflicts. Recommended adds compatible updates, supported settings restoration, and environment merging. Exact shows all achievable differences, but software removal and downgrades remain manual-only and high-risk actions are never preselected.

Extra applications are not automatically removed in the initial product. A future feature would require a separate threat review, per-item consent, reliable uninstall identity, rollback expectations, and explicit product approval.
