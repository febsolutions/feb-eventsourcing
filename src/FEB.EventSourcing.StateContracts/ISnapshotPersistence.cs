namespace FEB.EventSourcing.Snapshots;

/// <summary>A stored snapshot: the deserialized DTO and the stream version it represents.</summary>
public sealed record StoredSnapshot(object Snapshot, int StreamVersion);

/// <summary>
/// Provider-neutral snapshot persistence (one latest snapshot per aggregate).
/// Implemented by each persistence package (MongoDB, SQL, …); consumed by the
/// provider-neutral <c>SnapshotStore</c> decorator.
/// </summary>
public interface ISnapshotPersistence
{
    Task SaveAsync<TAggregate, TId>(
        object snapshot,
        Type snapshotType,
        TId id,
        int streamVersion,
        int schemaVersion,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the latest snapshot if it exists and its schema version matches; otherwise null.</summary>
    Task<StoredSnapshot?> LoadAsync<TAggregate, TId>(
        TId id,
        Type snapshotType,
        int expectedSchemaVersion,
        CancellationToken cancellationToken = default);
}
