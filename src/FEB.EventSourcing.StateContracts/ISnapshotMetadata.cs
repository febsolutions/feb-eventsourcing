namespace FEB.EventSourcing.Snapshots;

public interface ISnapshotMetadata
{
    Type AggregateType { get; }
    Type SnapshotType { get; }
    int Version { get; }
    
    object CreateSnapshot(object aggregate);
    void RestoreSnapshot(object aggregate, object snapshot);
}