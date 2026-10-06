using Microsoft.CodeAnalysis;

namespace FEB.EventSourcing.Snapshots.Generator;

internal static class Diagnostics
{
    public static readonly DiagnosticDescriptor AggregateNotPartial =
        new(
            id: "ASG001",
            title: "Aggregate must be partial",
            messageFormat: "Aggregate '{0}' is marked with [AutoSnapshot] but is not declared partial",
            category: "AutoSnapshot",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor PropertyNotWritable =
        new(
            id: "ASG002",
            title: "Snapshot property must be writable",
            messageFormat: "Property '{0}' on type '{1}' must have an accessible getter and setter for snapshot restore. If it is computed or intentionally transient, annotate it with [IgnoreSnapshot]",
            category: "AutoSnapshot",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InstanceFieldNotSupported =
        new(
            id: "ASG005",
            title: "Instance fields are not snapshotted",
            messageFormat: "Field '{0}' on type '{1}' would be silently lost by snapshotting. Model the state as a property with an accessible setter, or annotate the field with [IgnoreSnapshot] to declare it intentionally transient",
            category: "AutoSnapshot",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NoSnapshotProperties =
        new(
            id: "ASG003",
            title: "No snapshot properties found",
            messageFormat: "Aggregate '{0}' has [AutoSnapshot] but no snapshotable properties",
            category: "AutoSnapshot",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsupportedPropertyType =
        new(
            id: "ASG004",
            title: "Unsupported snapshot property type",
            messageFormat: "Property '{0}' on aggregate '{1}' has unsupported type '{2}' for snapshotting",
            category: "AutoSnapshot",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);
    
    public static readonly DiagnosticDescriptor CyclicReference =
        new(
            id: "ASG006",
            title: "Cyclic reference detected",
            messageFormat: "Cyclic reference detected in snapshot graph: {0}",
            category: "AutoSnapshot",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);
    
    public static readonly DiagnosticDescriptor TypeNotPartial =
        new(
            id: "ASG009",
            title: "Snapshot type must be partial",
            messageFormat: "Type '{0}' must be declared partial to support auto-generated snapshot methods",
            category: "AutoSnapshot",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);
}
