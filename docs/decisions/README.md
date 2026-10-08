# Architecture decisions

The guardrails in [CONTRIBUTING.md](../../CONTRIBUTING.md) are short on purpose; the
documents here explain *why* each one exists — what problem it solves, what was
rejected, and what it costs. Read the relevant one before proposing a change to it.

| # | Decision | Status |
|---|---|---|
| [0001](0001-optimistic-concurrency-is-the-correctness-anchor.md) | Optimistic concurrency is the only correctness anchor | Accepted |
| [0002](0002-separate-snapshots-and-state-cache.md) | Snapshots and the state cache are separate modules | Accepted |
| [0003](0003-redis-as-version-guarded-state-cache.md) | Redis caches aggregate state with version-guarded writes | Accepted |
| [0004](0004-fail-closed-snapshot-generator.md) | The snapshot generator is fail-closed | Accepted |
| [0005](0005-explicit-registration-no-appdomain-scans.md) | Explicit registration, no AppDomain scans | Accepted |
| [0006](0006-snapshot-cadence-as-crossing-check.md) | Snapshot cadence is a crossing check | Accepted |
| [0007](0007-mongodb-without-replica-set.md) | MongoDB works without a replica set | Accepted |
| [0008](0008-store-chain-ordered-by-layer.md) | The store chain is ordered by layer, not call order | Accepted |
| [0009](0009-outbox-named-subscribers.md) | The outbox tracks delivery per named subscriber | Accepted |
| [0010](0010-outbox-retention-and-ordering.md) | Outbox retention and dequeue ordering | Accepted |
| [0011](0011-version-stays-int.md) | `Version` stays `int` | Accepted |
| [0012](0012-relational-providers.md) | Relational providers share one core and one table set | Accepted |
| [0013](0013-optional-stable-event-names.md) | Stable event names are optional | Accepted |
| [0014](0014-generator-ships-in-statecontracts.md) | The generator ships inside StateContracts | Accepted |
| [0015](0015-outbox-written-atomically-with-the-events.md) | Outbox envelopes are written atomically with the events | Accepted |
| [0016](0016-caller-provided-transaction.md) | Relational stores can join a transaction provided by the caller | Accepted |
| [0017](0017-target-frameworks.md) | Packages target .NET 8 and .NET 10 | Accepted |

## Writing a new decision

Copy the structure of an existing document: **Context** (the problem and the forces
at play), **Decision** (what we do), **Consequences** (what follows, good and bad,
including what was rejected). Number it consecutively and add it to the table.

A decision is never edited to say something else: if it changes, write a new one and
set the old one's status to *Superseded by NNNN*.

Describe problems found in production **anonymously**: the technical failure and its
mechanism — never the affected application, organisation, customer data or figures
from a specific installation.
