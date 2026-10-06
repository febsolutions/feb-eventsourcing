namespace FEB.EventSourcing.Snapshots.Generator;

internal sealed record SnapshotProperty(
    string Name,
    string TypeName,              // FullyQualifiedFormat of the property type
    SnapshotKind Kind,
    SnapshotModel? NestedModel,   // for Object or List
    string? ElementTypeName,       // List only: FullyQualifiedFormat of T
    bool IsNullable
    
);