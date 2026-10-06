namespace FEB.EventSourcing.Snapshots;

/// <summary>
/// Provider-neutral snapshot decorator (<see cref="EventStoreLayer.Snapshots"/>). On
/// load: latest matching snapshot + delta events; after save: writes a new snapshot
/// when the cadence crosses a multiple of N events. Storage is delegated to the
/// registered <see cref="ISnapshotPersistence"/> (MongoDB, SQL, …). Transparent for
/// aggregates without <c>[AutoSnapshot]</c> metadata.
/// </summary>
public class SnapshotStore<TAggregate, TId>(
    ISnapshotPersistence snapshotPersistence,
    IEventStore<TAggregate, TId> innerStore,
    AggregateFactory<TAggregate, TId> aggregateFactory,
    ISnapshotMetadataRegistry? snapshotMetadataRegistry,
    EventStoreOptions options,
    ISnapshotWriteQueue? snapshotWriteQueue = null) : ProxyStore<TAggregate, TId>(innerStore)
    where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new()
{
    private readonly int _everyNEvents = options.SnapshotsEveryNEvents;
    private readonly ISnapshotMetadata? _meta = snapshotMetadataRegistry?.GetForAggregate(typeof(TAggregate));

    public override async Task<TAggregate?> LoadByIdAsync(TId id, CancellationToken cancellationToken = default)
    {
        if (_meta == null)
            return await base.LoadByIdAsync(id, cancellationToken);

        var stored = await snapshotPersistence.LoadAsync<TAggregate, TId>(id, _meta.SnapshotType, _meta.Version, cancellationToken);

        if (stored == null)
            return await base.LoadByIdAsync(id, cancellationToken);

        // Delta: only events AFTER the snapshot version (fromVersion is inclusive → +1)
        var events = await InnerStore.LoadEventsAsync(id, stored.StreamVersion + 1, cancellationToken);

        var aggregate = aggregateFactory.Create(id);
        _meta.RestoreSnapshot(aggregate, stored.Snapshot);
        aggregate.RestoreState(stored.StreamVersion, events.Select(e => e.Payload));

        return aggregate;
    }

    public override async Task SaveAsync(TAggregate aggregate, CommandContext context, CancellationToken cancellationToken = default)
    {
        var versionBeforeSave = aggregate.Version;

        await base.SaveAsync(aggregate, context, cancellationToken);

        if (_meta != null)
            await MaybeCreateSnapshotAsync(aggregate, versionBeforeSave, cancellationToken);
    }

    /// <summary>
    /// Cadence as a crossing check over the event count: a snapshot is due whenever a
    /// save crosses a multiple of N — even if one command writes several events and
    /// jumps over the multiple.
    /// </summary>
    internal static bool ShouldSnapshot(int versionBeforeSave, int versionAfterSave, int everyNEvents)
    {
        if (everyNEvents <= 0)
            return false;

        if (versionAfterSave <= versionBeforeSave)
            return false;

        // versions are 0-based => event count = version + 1
        return (versionAfterSave + 1) / everyNEvents > (versionBeforeSave + 1) / everyNEvents;
    }

    private async Task MaybeCreateSnapshotAsync(TAggregate aggregate, int versionBeforeSave, CancellationToken cancellationToken)
    {
        if (!ShouldSnapshot(versionBeforeSave, aggregate.Version, _everyNEvents))
            return;

        // Capture the state synchronously (the aggregate may change afterwards);
        // only the write may go to the background.
        var snapshot = _meta!.CreateSnapshot(aggregate);
        var id = aggregate.Id;
        var version = aggregate.Version;
        var schemaVersion = _meta.Version;
        var snapshotType = _meta.SnapshotType;

        if (snapshotWriteQueue != null)
        {
            snapshotWriteQueue.TryEnqueue(new SnapshotWriteJob(
                typeof(TAggregate).Name,
                ct => snapshotPersistence.SaveAsync<TAggregate, TId>(snapshot, snapshotType, id, version, schemaVersion, ct)));
            return;
        }

        await snapshotPersistence.SaveAsync<TAggregate, TId>(snapshot, snapshotType, id, version, schemaVersion, cancellationToken);
    }
}
