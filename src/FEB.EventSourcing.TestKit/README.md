# FEB.EventSourcing.TestKit

Test helpers for the FEB.EventSourcing framework. One line per application proves
that every `[AutoSnapshot]` aggregate survives a full snapshot roundtrip without
losing state:

```csharp
SnapshotContract.AssertRoundtripsAll(typeof(Order).Assembly);
```

Aggregates are filled with deterministic random data (recursively, including
base-class state), snapshotted, JSON-roundtripped and restored; failures name the
exact property path and list all `[IgnoreSnapshot]` members for review.
