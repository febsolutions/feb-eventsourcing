using System.Collections.Immutable;

namespace FEB.EventSourcing.Snapshots.Generator;

internal sealed record SnapshotModel(
    string Namespace,
    string AggregateName,
    string SnapshotTypeName,
    ImmutableArray<SnapshotProperty> Properties,
    int Version);