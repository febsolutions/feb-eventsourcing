# 0014 — The generator ships inside StateContracts

- **Status:** Accepted
- **Date:** 2026-10-06

## Context

The snapshot source generator was attached to `FEB.EventSourcing.StateContracts` only
through a project reference. That runs it while building the repository, but does not
put it into any package — and NuGet's default for package dependencies
(`exclude="Build,Analyzers"`) would have kept it from reaching applications even if it
had been packed. Applications consuming the packages therefore got no generated
snapshot code, while every build and test inside the repository passed.

## Decision

- The generator is packed into `FEB.EventSourcing.StateContracts` under
  `analyzers/dotnet/cs`; there is no separate generator package. Attributes and
  generator always travel together.
- Dependencies between the framework's own packages do not exclude analyzers
  (`PrivateAssets=none` as the default for project references in
  `Directory.Build.props`), so the generator reaches an application through any
  framework package, transitively.
- A **package smoke test** (`scripts/package-smoke-test.sh`, `tests/PackageConsumer`)
  packs everything and builds an application that references only
  `FEB.EventSourcing.Postgres`; it fails unless generated code appears. It runs in CI
  and before every release.

## Consequences

- Referencing any framework package is enough to use `[AutoSnapshot]`.
- Packaging defects are caught before publishing, not by users.
- Projects that never use `[AutoSnapshot]` still load the generator; it generates
  nothing for them.
