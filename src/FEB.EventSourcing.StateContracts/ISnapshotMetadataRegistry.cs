namespace FEB.EventSourcing.Snapshots;

public interface ISnapshotMetadataRegistry
{
    ISnapshotMetadata? GetForAggregate(Type aggregateType);
}