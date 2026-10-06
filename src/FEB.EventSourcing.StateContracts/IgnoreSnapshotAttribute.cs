namespace FEB.EventSourcing.Snapshots;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class IgnoreSnapshotAttribute : Attribute;