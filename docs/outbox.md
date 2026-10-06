# Outbox and subscriptions

The outbox is the framework's bridge to **everything outside the current process
whose availability is not guaranteed**: message brokers, third-party APIs, e-mail
gateways, other bounded contexts. It guarantees that every persisted event is
delivered *at least once* to every registered consumer — even if the consumer is
down when the event is saved, and even if the process crashes in between.

This chapter explains the mental model first, then the moving parts, then the
scenarios you will meet when building an application on top of it.

## 1. Why an outbox at all?

`SaveAsync` appends events to the persistence. If you called an external system directly
inside the same request, you would face the classic dual-write problem: the events
are saved but the external call fails (or vice versa), and there is no transaction
that spans both. The outbox solves this by making "notify the outside world" part of
the same durable write:

1. `SaveAsync` appends the events **and** enqueues one outbox envelope per event.
2. A background worker later leases envelopes and delivers them.
3. Delivery state is tracked durably; failed deliveries are retried; hopeless ones
   are dead-lettered for inspection.

Nothing outside the process is called on the command path. Your command stays fast
and either fully succeeds or fully fails.

## 2. The three roles

| Role | Interface / type | Provided by | You implement it when… |
|---|---|---|---|
| **Persistence** — durable queue with per-subscriber delivery state | `IOutboxPersistence` | `UseMongoOutbox()` (MongoDB) or `UseSqlOutbox()` (PostgreSQL / SQL Server) | practically never |
| **Pump** — leases envelopes, calls subscribers, records results | `OutboxWorker` (a `BackgroundService`) | `es.UseOutboxWorker()` | you need different scheduling (rare) |
| **Subscriber** — a named consumer of events | `IOutboxSubscriber` | default: `DefaultOutboxDispatcher` (name `"handlers"`) | you integrate a broker, an API, another system |

Almost every application only ever touches the third role.

## 3. Subscribers: one, several, or a broker

A subscriber is a small class:

```csharp
public sealed class RabbitMqPublisher(IModel channel) : IOutboxSubscriber
{
    public string Name => "rabbitmq";   // stable! see 3.3

    public Task DispatchAsync(OutboxEnvelope envelope, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(envelope.Payload.Data);
        var props = channel.CreateBasicProperties();
        props.MessageId = envelope.Metadata.EventId.ToString();   // lets consumers dedupe
        props.Type = envelope.Payload.EventType;
        channel.BasicPublish("domain-events", envelope.Metadata.AggregateType, props, body);
        return Task.CompletedTask;
    }
}
```

Registration:

```csharp
es.UseMongoOutbox();            // or es.UseSqlOutbox()
es.UseOutboxSubscriber<RabbitMqPublisher>();
es.UseOutboxWorker();
```

### 3.1 The three standard setups

**A) Default — in-process async handlers only.**
You call nothing but `UseMongoOutbox()` (or `UseSqlOutbox()`) + `UseOutboxWorker()`. The framework registers
the default subscriber `"handlers"`, which delivers every event to all
`IASyncEventHandler<TEvent>` implementations found by the assembly scan. Use this for
side effects that must not block or fail the command (send e-mail, update a search
index, call an internal service).

**B) A message broker.**
You have RabbitMQ / Kafka / Azure Service Bus. Register **one** subscriber that
publishes to it (as above). The broker then does the fan-out to any number of
downstream consumers — that is what brokers are good at (independent queues, per
consumer retry, replay, new consumers with backfill). Do not model your downstream
consumers as outbox subscribers in that case; the outbox's job is only "get it into
the broker at least once".

If you *also* want in-process handlers, add the default explicitly:

```csharp
es.UseDefaultOutboxSubscriber();          // "handlers"
es.UseOutboxSubscriber<RabbitMqPublisher>(); // "rabbitmq"
```

**C) No broker — the lightweight subscription system.**
You have a handful of external targets and no broker. Register one subscriber per
target:

```csharp
es.UseDefaultOutboxSubscriber();
es.UseOutboxSubscriber<CrmSyncSubscriber>();      // "crm"
es.UseOutboxSubscriber<PartnerApiSubscriber>();   // "partner-api"
```

Each is tracked, retried and dead-lettered **independently** — the property this
whole design exists for. This scales comfortably to a few subscribers. If you find
yourself registering ten, it is time for setup B.

### 3.2 What "independent" means precisely

For every envelope the persistence keeps a delivery record **per subscriber name**:

```
outbox document
├─ Id, PayloadJson, MetadataJson, CreatedAt
├─ Deliveries
│   ├─ "handlers":    { DispatchedAt: 2026-08-14T10:00:01Z }
│   ├─ "crm":         { DispatchedAt: 2026-08-14T10:00:01Z }
│   └─ "partner-api": { AttemptCount: 3, LastError: "...", DeadLetteredAt: … }
├─ DispatchedAt   ← completion marker: set when every known subscriber is terminal
└─ LockedUntil    ← lease
```

The worker's loop, per batch:

1. Lease envelopes that are not completed and not currently leased.
2. For each envelope, call **only** the subscribers whose delivery is not yet
   terminal (not dispatched, not dead-lettered).
3. After each call, record the result for **that subscriber only**: success →
   `DispatchedAt`; failure → `AttemptCount++`, `LastError`, lease released so the
   next poll retries.
4. When a subscriber's `AttemptCount` reaches `MaxAttempts`, only **that
   subscription** is dead-lettered (a document `"{EventId}:{subscriber}"` in the
   dead-letter collection). The others are unaffected.
5. When every registered subscriber is terminal, the envelope is completed and
   never leased again.

Consequences you can rely on:

- A subscriber that succeeded is **never called again** for that envelope, no matter
  how often the others fail. (Exception: a crash between the successful call and
  writing `DispatchedAt` — see 4.1.)
- A dead subscriber (broker down for an hour) does not block the others and does not
  block the queue: its envelopes stay pending only *for it*, up to `MaxAttempts`.
- Retries are per envelope and per subscriber, driven by the polling interval — there
  is no exponential backoff yet; tune `MaxAttempts` × `PollingInterval` to the
  outage window you want to survive.

### 3.3 Subscriber names are contracts

The `Name` is the persistence key. Treat it like a database column name:

- Stable across deployments. Renaming = a brand-new subscriber that only sees
  envelopes created after the rename; the old name's pending deliveries are simply
  never picked up again (they do not block completion, because completion only
  considers *registered* names).
- Unique within the application. Two subscribers with the same name are collapsed
  to the first registered.
- Allowed characters: no `.`, no `$`, no whitespace (they are used as persistence
  keys — MongoDB field paths, JSON keys).
  The worker validates this at startup and throws.

### 3.4 Adding a subscriber later

A new subscriber only receives envelopes **created after** it was registered. There
is deliberately no automatic backfill — replaying the entire history into a new
consumer is a conscious operational decision. If you need it, do it explicitly:
load events via `IEventStore.LoadEventsAsync` / `RebuildProjectionsAsync`-style
iteration and push them yourself, or (setup B) let the broker replay.

## 4. Delivery guarantees — what to build for

### 4.1 At-least-once, therefore idempotent

Every subscriber **must be idempotent**. The guarantee is at-least-once: if the
process dies after your `DispatchAsync` returned but before the persistence recorded
`DispatchedAt`, the envelope will be delivered to you again after the lease expires.

Idempotency handles:

- `envelope.Metadata.EventId` — globally unique per event. Use it as the message id
  for brokers (`MessageId`), as an idempotency key for HTTP APIs, or in a small
  "already processed" set on your side.
- `envelope.Metadata.AggregateId` + `Version` — unique per stream position, handy
  when the target keeps per-aggregate state.

The in-process default subscriber has the same property: `IASyncEventHandler`
implementations must tolerate duplicates.

### 4.2 Ordering

Envelopes are leased oldest-first (`CreatedAt`), and within one aggregate the
version order matches creation order. But: the worker processes a *batch*, retries
happen out of order relative to newer envelopes, and multiple worker instances (one
per app replica) run concurrently. **Do not assume global ordering across
aggregates, and do not assume strict ordering after a failure.** If a subscriber
needs per-aggregate order, make it tolerant (e.g. compare versions and ignore
out-of-date updates) or route by aggregate id into an ordered broker partition.

### 4.3 Multiple app instances

Every replica hosts its own `OutboxWorker`. They coordinate through the lease
(`LockedUntil`, atomic `FindOneAndUpdate`): an envelope is processed by one worker
at a time. A crashed worker's lease expires after `LeaseDuration` (default 5 min)
and another instance picks the envelope up. Set the lease longer than your slowest
subscriber's realistic worst case.

## 5. Failure handling and operations

| Situation | What happens | What you do |
|---|---|---|
| Subscriber throws | attempt counted, lease released, retried on next poll | nothing — unless it keeps happening |
| Subscriber exhausted `MaxAttempts` | that subscription dead-lettered; others unaffected; envelope completes when all are terminal | inspect `outbox_deadletter` (fields `EventId`, `Subscriber`, `LastError`, payload), fix the target, re-publish manually |
| Broker down for a while | its subscription retries up to `MaxAttempts × PollingInterval` | size `MaxAttempts` for your outage tolerance |
| Worker crashes mid-batch | lease expires, another worker continues; succeeded subscribers are already recorded | nothing |
| No subscriber registered | worker logs a warning and idles | register one (`UseOutboxWorker` adds the default only if none exists) |

Options:

```csharp
es.UseMongoOutbox(o =>          // or es.UseSqlOutbox(o => …) with the same options
{
    o.SetMaxAttempts(10);                          // per subscription
    o.SetLeaseDuration(TimeSpan.FromMinutes(5));
    o.SetOutboxCollectionName("outbox");
    o.SetDeadLetterCollectionName("outbox_deadletter");
});

es.UseOutboxWorker(o =>
{
    o.SetBatchSize(50);
    o.SetPollingInterval(TimeSpan.FromSeconds(1));
    o.SetErrorBackoff(TimeSpan.FromSeconds(2));    // after an unexpected loop failure
});
```

Monitoring: watch the size of `outbox` where `DispatchedAt == null` (backlog) and
the growth of `outbox_deadletter`. Both are plain collections/tables — a Grafana
panel per subscriber name is a few lines of aggregation.

## 6. Retention: completed envelopes are cleaned up

A delivered envelope is first **marked** completed (`DispatchedAt`, plus the
per-subscriber state) — it does not vanish at that moment. Removal happens through
the built-in retention cleanup, **default 7 days** after completion:

- **MongoDB**: a TTL index on `DispatchedAt` (`ttl_dispatched`) — the server deletes
  expired envelopes itself, no worker involvement. Pending envelopes have
  `DispatchedAt = null` and are never touched by TTL.
- **PostgreSQL / SQL Server**: the `OutboxWorker` periodically issues a bounded
  `DELETE` (default every 5 min, max 1000 rows per run).

Configuration (per provider options):

```csharp
es.UseMongoOutbox(o => o.SetCompletedRetention(TimeSpan.FromDays(30)));
// or keep forever (pre-9.0.0-beta.3 behavior):
es.UseMongoOutbox(o => o.DisableCompletedCleanup());

es.UseSqlOutbox(o =>
{
    o.SetCompletedRetention(TimeSpan.FromDays(30));
    o.SetCleanupBatchSize(1000);
});
es.UseOutboxWorker(o => o.SetCleanupInterval(TimeSpan.FromMinutes(5)));
```

What is **never** cleaned up: pending envelopes (obviously) and the
**dead-letter collection/table** — dead letters are incidents that require a human
decision; remove them explicitly once handled. Custom `IOutboxPersistence`
implementations can opt in by overriding `CleanupCompletedAsync` (default no-op).

## 7. Ordering guarantees, precisely

Dequeue is **oldest-first by `CreatedAt`** among envelopes that are pending and not
leased — verified per provider by the contract test
`Dequeue_delivers_envelopes_strictly_oldest_first`. What can make delivery *appear*
out of order:

- **Retries**: a failing envelope stays pending (oldest) while newer ones complete —
  its age grows until it succeeds or dead-letters after `MaxAttempts`. A monitoring
  metric like "oldest pending" is dominated by such stragglers.
- **Lease expiry**: an envelope taken by a crashed/stalled worker only returns after
  `LeaseDuration` (default 5 min) and is then processed late.
- **Multiple worker instances** interleave batches — global order across instances
  is not defined.
- "Oldest pending is N minutes old" during a backlog drain is usually just the FIFO
  frontier: the remaining envelopes are the newest-created ones, and the oldest of
  *those* is as old as its own `CreatedAt` — not evidence of disorder. To check for
  real disorder, compare `Deliveries.<name>.DispatchedAt` order against `CreatedAt`
  order.

## 8. Envelope contents

`OutboxEnvelope` carries what a subscriber needs to forward the event faithfully:

- `Payload.EventType` — the event's stable `[EventName]` name if it has one,
  otherwise its assembly-qualified CLR type name (see
  [concepts → stable storage names](concepts.md#stable-storage-names-eventname)).
  Giving your events names makes this field a contract you control, which matters
  most here: it is what external consumers and brokers see.
- `Payload.Data` — the event serialized as JSON with its **runtime** type (all
  properties present).
- `Metadata` — `EventId`, `AggregateType`, `AggregateId` (as string), `Version`,
  `OccurredAt`, `TenantId`, `UserId`, `CorrelationId`, `CausationId` (the command
  id), `Headers`.

The default subscriber deserializes `Data` back into the CLR type to call typed
handlers. A broker publisher usually forwards `Data` and `EventType` as-is and puts
`EventId`/`CorrelationId` into message properties.

If your own subscriber needs the CLR type, resolve it with
`EventTypeNames.ResolveRequired(envelope.Payload.EventType)` rather than
`Type.GetType`: it understands `[EventName]` names and aliases, falls back to CLR
names for older envelopes, and fails with a message that names the remedy.

```csharp
public sealed class ArticleChangedPublisher(IBus bus) : IOutboxSubscriber
{
    public string Name => "article-changed-publisher";

    public async Task DispatchAsync(OutboxEnvelope envelope, CancellationToken ct)
    {
        var type = EventTypeNames.ResolveRequired(envelope.Payload.EventType);
        var @event = (IEvent)JsonSerializer.Deserialize(envelope.Payload.Data, type)!;
        await bus.PublishAsync(envelope.Payload.EventType, @event, ct);
    }
}
```

## 9. Compatibility and upgrade

- **Existing envelopes** written before per-subscriber tracking existed have no
  `Deliveries` map. They are treated as *pending for every registered subscriber*
  and completed the normal way — no migration step.
- **`IOutboxDispatcher`** (the previous single-consumer interface) still works:
  `es.UseOutboxDispatcher<T>()` wraps it as the *only* subscriber, named after the
  type. New code should implement `IOutboxSubscriber`.
- The dead-letter document id changed from `EventId` to `"{EventId}:{subscriber}"`
  (one entry per failed subscription).

## 10. Checklist for a new subscriber

1. Pick a stable, unique `Name` (no `.`, `$`, whitespace).
2. Make `DispatchAsync` idempotent using `EventId`.
3. Let exceptions propagate — that is how the worker knows to retry. Do not swallow.
4. Do not assume ordering across aggregates.
5. Register with `UseOutboxSubscriber<T>()`; if you also want in-process handlers,
   add `UseDefaultOutboxSubscriber()`.
6. Decide consciously whether the new subscriber needs historical events (no
   automatic backfill).
7. Put `outbox_deadletter` on a dashboard.
