# WPF migration and recovery UI

Replica uses one unelevated WPF Shell sized for 1280×720 and larger displays. WPF device-independent units provide DPI scaling, every page remains vertically scrollable, and asynchronous commands keep scan, archive, network, execution, history, and rollback work off the UI thread.

## Workspace

The persistent left navigation exposes Home, read-only Scan, Snapshot Builder, Comparison, Diff Viewer, Restore Plan, Recovery Wizard, Execution, Result, History, Settings, About, and GitHub Updates. `Ctrl+1` through `Ctrl+6` cover the primary workflow, `Ctrl+,` opens Settings, and access keys remain available on navigation and primary actions.

Home offers direct entry points for all three Snapshot types, opening a Snapshot, post-reset recovery, recent history, similarity, and updates. Scan reports the active stage, progress, discovered counts, partial-failure warnings, and cancellation. Snapshot Builder exposes inventory categories, explicit folder categories, sensitive exclusions, optional encryption, destination, size estimation, and an approval checkbox. Changing scope, encryption, selected folders, or destination invalidates prior approval.

Comparison presents overall and category similarity. Diff Viewer supplies a tree, search, difference filter, source/current values, risk, elevation, restart, recommendation, and user selection. Restore Plan separates Safe, Recommended, and Exact policy from its Dry Run and plan review. Approving the Dry Run does not execute it: Execution requires a second explicit confirmation, reports progress and bounded summaries, and sends outcomes to Result. Recovery Wizard retains its nineteen-step state machine, hardware and drive review, explicit restart/resume, verification, and manual authentication work. History combines Snapshot comparison, past-state plans, restore records, and rollback preview/approval.

## Accessibility and localization

- interactive regions have UI Automation names and live status regions;
- tab navigation, access keys, keyboard shortcuts, and visible keyboard focus are supported;
- headings expose automation heading levels and status always includes text rather than color alone;
- Light and Dark resource dictionaries use the same semantic brush keys;
- Windows high-contrast mode substitutes system-color resources;
- Korean is loaded by default, while the same resource keys have an `en-US` dictionary for continued English expansion.

## Safety boundary

Scan, Snapshot validation, comparison, filtering, and Dry Run do not mutate the machine. Snapshot creation writes only the user-selected destination after a fresh read-only scan and explicit approval; Recovery file collection includes only selected folders. Restore execution accepts only an approved typed plan, asks again immediately before execution, journals before mutation, and never automatically removes Extra applications, downgrades software, restores credentials, or reboots Windows. Retry creates another review boundary instead of silently repeating failed work.

Automated UI tests use fake scanners, writers, settings, GitHub services, and rollback/execution adapters. They do not install software or change the real registry, environment, PATH, fonts, or user files.
