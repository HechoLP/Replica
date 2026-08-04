# ADR 0002: .NET 10 and WPF desktop architecture

- Status: Accepted
- Date: 2026-08-04

## Context

Replica is a Windows 11 system utility that needs mature access to winget, registry, environment, filesystem, process, elevation, packaging, and desktop UI capabilities. It should ship self-contained and present one native-feeling desktop application.

## Decision

Use C# on .NET 10 LTS with WPF and MVVM. Target `net10.0-windows` and initially publish self-contained `win-x64`. Use Core, Infrastructure, App, and built-in plugin class-library boundaries; keep business logic out of WPF code-behind. Use SQLite for local indexes/history, `System.Text.Json` for JSON, Inno Setup for the installer, and xUnit for automated testing.

Enable nullable reference types, warnings as errors, deterministic builds, central package management, and formatting enforcement. Use additional dependencies only where they materially reduce risk or boilerplate.

## Consequences

The implementation has direct, well-supported Windows interop and a mature desktop binding model. It is Windows-only, WPF testing requires disciplined view-model separation, and initial artifacts target x64 rather than every Windows architecture. Moving to another UI stack or adding Arm64 requires a later ADR and compatibility plan.
