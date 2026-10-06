namespace FEB.EventSourcing.Snapshots;

public enum SnapshotCompression
{
    None = 0,
    Lz4 = 1,
    Zstd = 2
}