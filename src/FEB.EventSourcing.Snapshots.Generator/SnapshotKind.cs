namespace FEB.EventSourcing.Snapshots.Generator;

internal enum SnapshotKind
{
    Direct,     // primitive/record/guid/...
    Object,     // class -> eigener Snapshot
    List        // List<T> -> List<TSnapshot>
}