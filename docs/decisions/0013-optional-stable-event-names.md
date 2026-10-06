# 0013 — Stable event names are optional

- **Status:** Accepted
- **Date:** 2026-10-05

## Context

Every persistence stored the event's CLR type name with the payload — MongoDB as the
BSON discriminator (short class name), SQL and the outbox as the assembly-qualified
name. That ties stored data to the class: moving an event to another namespace or
project, or renaming it, makes old events unreadable. Outbox payloads expose the CLR
name to external consumers as well.

## Decision

- `[EventName("order.placed")]` gives an event a **stable storage name** used by all
  three places: MongoDB discriminator, SQL `event_type`, outbox `EventType`.
- The attribute is **optional**: events without it keep the previous behaviour, so
  existing applications need no change and can adopt it event by event.
- Adopting it needs **no data migration**: the CLR short name stays readable on
  MongoDB, and SQL/outbox values that no event claims are still interpreted as CLR
  type names. `Aliases` (read-only) cover names that no longer match the class.
- Duplicate names or aliases **fail at start-up**.
- There is no equivalent for aggregates: collection and `aggregate_type` names derive
  from the aggregate's class name, and renaming them is a data migration, not a
  discriminator change.

## Consequences

- Event classes can be moved freely once named.
- Outbox consumers should resolve types with `EventTypeNames.ResolveRequired`, which
  understands names, aliases and CLR names.
- Without the attribute, event classes remain pinned by their CLR names.
