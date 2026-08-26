# WPF migration and recovery UI

Replica uses one unelevated WPF Shell that can shrink to 1024×680. WPF device-independent units provide DPI scaling, every page remains vertically scrollable, and asynchronous commands keep scan, archive, network, execution, history, and rollback work off the UI thread.

## Workspace

The persistent left navigation exposes beginner tasks: Start, current-PC inspection, Snapshot creation, opening a Snapshot to compare/restore, post-reset recovery, and history. Comparison, item selection, plan review, execution, and result are sequential workflow stages instead of top-level implementation labels. `Ctrl+1` through `Ctrl+5` cover the primary entry points, `Ctrl+,` opens Settings, and access keys remain available on navigation and primary actions.

Start explains the recommended three-step workflow and offers all three Snapshot types. Offline Recovery Pack adds provenance and licensing fields, a trusted-installer picker, and a review table showing publisher, certificate-bound signature result, version, architecture, size, source, and SHA-256. Scan reports the active stage, progress, discovered counts, partial-failure warnings, and cancellation. Snapshot creation exposes inventory categories, explicit folder categories, sensitive exclusions, optional encryption, destination, size estimation, and an approval checkbox. Recovery and Offline creation require a current estimate; changing scope, encryption, selected folders, installers, or destination invalidates prior approval.

Comparison presents overall and category similarity. Item selection supplies a tree, search, difference filter, source/current values, risk, elevation, restart, recommendation, and user selection. Plan review separates Safe, Recommended, and Exact policy from its dry run. Approving the dry run does not execute it: restoration requires a second explicit confirmation, reports progress and bounded summaries, and sends outcomes to the result stage. Recovery retains its nineteen-step internal state machine but displays five phases, hardware and drive review, explicit restart/resume, verification, and manual authentication work. History combines Snapshot comparison, past-state plans, restore records, and exact-target rollback preview/approval.

## Accessibility and localization

- interactive regions have UI Automation names and live status regions;
- tab navigation, access keys, keyboard shortcuts, and visible keyboard focus are supported;
- headings expose automation heading levels and status always includes text rather than color alone;
- Light and Dark resource dictionaries use the same semantic brush keys;
- Windows high-contrast mode substitutes system-color resources;
- Korean is the currently supported display language. The incomplete `en-US` resource dictionary remains an internal expansion aid and is not offered as a complete language option.

## Safety boundary

Scan, Snapshot validation, comparison, filtering, and Dry Run do not mutate the machine. Snapshot creation writes only the user-selected destination after a fresh read-only scan and explicit approval; Recovery file collection includes only selected folders. Offline installers must be Windows-trusted and are rebound to reviewed hashes, sizes, and publisher certificates at creation and export; exporting never launches them. Restore execution accepts only an approved typed plan, asks again immediately before execution, journals before mutation, and never automatically removes Extra applications, downgrades software, restores credentials, or reboots Windows. Retry creates another review boundary instead of silently repeating failed work.

Automated UI tests use fake scanners, writers, settings, GitHub services, and rollback/execution adapters. They do not install software or change the real registry, environment, PATH, fonts, or user files.
