# CLAUDE.md — instructions for AI coding agents

This repository contains the FEB.EventSourcing framework (`src/FEB.EventSourcing.*`,
`src/FEB.Cqrs`), published as NuGet packages and used in production applications.

**[CONTRIBUTING.md](CONTRIBUTING.md) is binding** — its rules on tests, coverage,
documentation and the architecture guardrails apply to every change you make. In
addition:

## Working rules

- Before calling a change done: `dotnet build FEB.EventSourcing.sln` without errors,
  `dotnet test src/FEB.EventSourcing.Tests` green, and `./scripts/coverage.sh` at or
  above 85 % line coverage. Update [docs/testing.md § 6](docs/testing.md#6-test-coverage)
  (table, test count, date) when coverage numbers change.
- Changes to packaging (`*.csproj`, `Directory.Build.props`, the generator) must pass
  `./scripts/package-smoke-test.sh` — unit tests cannot see packaging defects.
- Bug found → first a test that proves it, then the fix.
- Design decisions with more than one reasonable option: present the options with a
  recommendation and let the maintainer decide — do not guess.
- Keep everything in English; never write customer data, internal hostnames or
  credentials into code, tests, sample data, documentation or commit messages.
- Architectural decisions are recorded as documents in `docs/decisions/` (next number,
  format in its README); a changed decision supersedes the old document instead of
  editing it.
- Problems found in production are documented **anonymously** — the technical failure
  and its mechanism, never the affected application, organisation, customer data or
  figures from a specific installation.

## Git

- Work on a feature branch and integrate through a pull request against `main`.
- Never push, tag or publish without an explicit request. Tags trigger the release
  workflow, which publishes to nuget.org.
- Never commit build output (`**/bin/`, `**/obj/`, `coverage/`, `artifacts/`).

## Useful commands

```bash
dotnet build FEB.EventSourcing.sln
dotnet test src/FEB.EventSourcing.Tests          # full suite (Docker required)
./scripts/coverage.sh                            # suite + coverage gate
./scripts/package-smoke-test.sh                  # pack + consume the packages
dotnet run --project samples/OrderSample         # runnable example
```

## Where things are

| Topic | Location |
|---|---|
| User documentation | `docs/README.md` (index) |
| Architecture decisions | `docs/decisions/` |
| Framework tests | `src/FEB.EventSourcing.Tests` |
| Package smoke test | `tests/PackageConsumer` |
| Example | `samples/OrderSample` |
| CI / release | `.github/workflows/ci.yml`, `.github/workflows/release.yml` |
| Shared package metadata | `Directory.Build.props` |
