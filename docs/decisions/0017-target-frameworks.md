# 0017 — Packages target .NET 8 and .NET 10

- **Status:** Accepted
- **Date:** 2026-10-08

## Context

Up to 9.3 every package targeted only `net8.0`. .NET 8 reaches the end of support on
10 November 2026; .NET 10 is the current long-term support release (supported until
November 2028). Applications using the framework move to .NET 10 at their own pace —
some will still run on .NET 8 for a while.

A `net8.0` package already runs on .NET 10, so a `net10.0` target is not needed for
compatibility. It is needed so that the framework is built, tested and packed against
the runtime applications actually move to, instead of only being assumed to work there.

Two options were considered:

- **`net10.0` only** — simplest build, but a major release: every application on
  .NET 8 would have to upgrade its runtime before it could take any further framework
  release, including fixes.
- **`net8.0` and `net10.0`** — packages contain both; nothing changes for applications
  on .NET 8, applications on .NET 10 get assets built and tested for it.

## Decision

- Every package targets **`net8.0;net10.0`**, defined once as `FebTargetFrameworks` in
  `Directory.Build.props`. The snapshot generator stays on `netstandard2.0` with
  Roslyn 4.8 (it runs inside the compiler and IDEs, not on the application runtime);
  it ships once under `analyzers/dotnet/cs`.
- The test suite and the package smoke test run **once per target framework**; the
  85 % coverage gate applies to each run.
- Building the repository requires the **.NET 10 SDK** (`global.json`); running the
  `net8.0` tests additionally needs the .NET 8 runtime.
- Dependencies keep their **lowest supported versions** (for example
  `Microsoft.Extensions.*` 8.x) for both targets; applications on .NET 10 resolve
  newer versions through their own references.
- `net8.0` is removed in a **major release**, once no supported application depends on
  it. That release gets an entry in the upgrade guide.

## Consequences

- No action for applications: NuGet picks the `net10.0` assets on .NET 10 and the
  `net8.0` assets on .NET 8.
- Code has to compile for both targets, so language features are limited to C# 12
  until `net8.0` is dropped; framework APIs newer than .NET 8 need `#if NET10_0_OR_GREATER`
  — none are used so far.
- CI time for tests roughly doubles; both framework runs execute in parallel.
- The `net10.0` build made one gap visible: the default NuGet audit checks only
  direct packages for `net8.0` projects. The repository now audits transitive packages
  for every target (`NuGetAuditMode=all`), so vulnerable packages pulled in by a
  dependency no longer go unnoticed.
