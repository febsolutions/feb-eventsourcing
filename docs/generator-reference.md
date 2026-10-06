# Generator reference

The `FEB.EventSourcing.Snapshots.Generator` source generator processes every class
marked `[AutoSnapshot]` and emits snapshot DTOs, `CreateSnapshot()` /
`RestoreFromSnapshot()` methods, `ISnapshotMetadata` implementations and a
per-assembly registration module. It ships as an analyzer inside the
`FEB.EventSourcing.StateContracts` package — referencing that package is all it
takes.

## Attributes

| Attribute | Target | Effect |
|---|---|---|
| `[AutoSnapshot]` | class | Generate snapshot support for this aggregate (and, transitively, for its nested state types) |
| `[IgnoreSnapshot]` | property, field | Exclude the member from snapshotting — a documented decision, listed by the TestKit |

Both live in namespace `FEB.EventSourcing.Snapshots`.

## What is generated

For `[AutoSnapshot] public partial class Order` with a nested `partial class OrderLine`:

```csharp
// Order.g.cs
public sealed class OrderSnapshot { public string Customer { get; init; } ... public List<OrderLineSnapshot> Lines { get; init; } }
internal class OrderSnapshotMeta : ISnapshotMetadata { AggregateType, SnapshotType, Version (hash), CreateSnapshot(object), RestoreSnapshot(object, object) }
public partial class Order { public OrderSnapshot CreateSnapshot(); public void RestoreFromSnapshot(OrderSnapshot s); }

// OrderLine.g.cs
public sealed class OrderLineSnapshot { ... }
public partial class OrderLine { CreateSnapshot(); RestoreFromSnapshot(...); }

// SnapshotMetadataModule.g.cs (namespace FEB.EventSourcing.Snapshots.Generated)
public static class SnapshotMetadataModule_<AssemblyName> { public static IReadOnlyList<ISnapshotMetadata> CreateAll(); }
```

Nested types shared by several aggregates (e.g. an `Address` used by `Customer` and
`Supplier`) are generated once. `CreateSnapshot()` deep-copies lists and nested
objects — the DTO never aliases the aggregate's collections.

The generated files can be inspected under
`obj/<Configuration>/<TFM>/FEB.EventSourcing.Snapshots.Generator/…` (or via the
IDE's "Analyzers → Source Generators" node).

## Supported property types

| Category | Types | Handling |
|---|---|---|
| Direct | primitives, `string`, `decimal`, enums, `Guid`, `DateTime`, `DateTimeOffset` — and their nullable forms | copied by value |
| Nested object | any `partial` class/record declared in the **same compilation** | own generated snapshot model; deep copy on create/restore; `null` preserved |
| Lists | `List<T>`, `IReadOnlyList<T>`, `ICollection<T>` where `T` is a direct type or a nested partial class | copied element-wise (new list instance on restore) |
| Base-class properties | properties declared in source-level base classes | included; the walk stops at the first externally compiled base (the framework's `AggregateRoot`) |

Not supported — each is a compile error (`ASG004`), so use `[IgnoreSnapshot]` or
restructure the state:

- `Dictionary<,>` and other collections than the three above, arrays,
- classes from *other* assemblies (no source available to generate for),
- interfaces / abstract types as property types,
- computed properties (`ASG002`), instance fields (`ASG005`).

## The fail-closed principle

Every member of a snapshotted type is either **mapped**, `[IgnoreSnapshot]`, or a
compile **error**. Nothing is skipped silently. Combined with the TestKit roundtrip
assertion this guarantees that no aggregate state can be lost by snapshotting
unnoticed. Expect a few `ASG00x` errors the first time you annotate an existing
aggregate — each is a real decision ("is this state or derived?").

## Schema version

Each snapshot model gets an `int` version derived from a SHA-256 hash over the
property names and fully-qualified types, **recursively including nested models**.
Any shape change (add/remove/rename/retype a property anywhere in the graph)
changes the version. Stores treat a version mismatch as "no snapshot" (full replay);
the Redis cache treats it as a miss. The number itself is not meaningful — do not
store or compare it manually.

## Diagnostics

| Id | Severity | Trigger | Fix |
|---|---|---|---|
| ASG001 | Error | `[AutoSnapshot]` on a class that is not `partial` | add `partial` |
| ASG002 | Error | property without an accessible getter/setter — computed properties (`=> …`), get-only auto-properties, `private set` in a base class | add a setter (state), or annotate `[IgnoreSnapshot]` (derived/transient) and recompute in `OnAfterRehydrate` |
| ASG003 | Warning | aggregate has no snapshotable properties | remove `[AutoSnapshot]` or add state |
| ASG004 | Error | unsupported property type | restructure (e.g. list of pairs instead of dictionary; nested partial class instead of external type) or `[IgnoreSnapshot]` |
| ASG005 | Error | instance field (would be silently lost) | model as a property, or `[IgnoreSnapshot]` for caches/locks |
| ASG006 | Error | cyclic reference in the snapshot graph (A has B has A) | break the cycle (reference by id) |
| ASG009 | Error | nested state type is not `partial` | add `partial` |

Examples:

```csharp
[AutoSnapshot]
public partial class Account : AggregateRoot<Account, string>
{
    public decimal Balance { get; set; }                       // mapped

    public bool IsOverdrawn => Balance < 0;                    // ASG002 → derived:
    // [IgnoreSnapshot] public bool IsOverdrawn => Balance < 0;  ✓

    private readonly object _lock = new();                     // ASG005 → not state:
    // [IgnoreSnapshot] private readonly object _lock = new();   ✓

    public Dictionary<string, decimal> Limits { get; set; }    // ASG004 → restructure:
    // public List<Limit> Limits { get; set; } with partial class Limit { Key, Value } ✓

    public Address Home { get; set; }                          // ASG009 if Address is not partial
}
```

## Hosting notes

The generator targets `netstandard2.0` and Roslyn 4.8, so it loads in every host
(dotnet CLI, Rider, Visual Studio, VS Code/OmniSharp). If your IDE shows generated
types as missing after upgrading the package, reload the project or restart the IDE
to clear its analyzer cache — the command-line build is authoritative.

The generator emits no NRT warnings into the consuming project (`= default!` on
DTO properties, targeted `!` on create expressions); consumers can build with
`TreatWarningsAsErrors`.
