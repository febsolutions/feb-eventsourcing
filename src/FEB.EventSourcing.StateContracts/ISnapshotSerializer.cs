namespace FEB.EventSourcing.Snapshots;

public interface ISnapshotSerializer
{
    byte[] Serialize<T>(T snapshot, Type type, SnapshotCompression compression);
    object Deserialize(byte[] payload, Type snapshotType, SnapshotCompression compression);
}
