using System.Collections.Concurrent;

namespace FEB.EventSourcing.InMemory;

public sealed class InMemoryEventStorePersistence : IEventStorePersistence
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<object, StreamState>> _streams = new();

    private static string GetStreamKeyLevel1<TAggregate>() => typeof(TAggregate).FullName!;

    public Task AppendEventsAsync<TAggregate, TId>(
        TId id,
        int expectedVersion,
        ICollection<EventEnvelope<TAggregate, TId>> events,
        CancellationToken cancellationToken = default)
        where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new()
    {
        //var streamKey = GetStreamKey<TAggregate, TId>(id);
        var streamList = _streams.GetOrAdd(GetStreamKeyLevel1<TAggregate>(), _ => new ConcurrentDictionary<object, StreamState>());
        var stream = streamList.GetOrAdd(id!, _ => new StreamState());

        lock (stream.SyncRoot)
        {
            var currentVersion = stream.Events.Count - 1;

            if (currentVersion != expectedVersion)
                throw new ConcurrencyException<TId>(id, expectedVersion, currentVersion);

            foreach (var envelope in events)
            {
                stream.Events.Add(envelope);
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<EventEnvelope<TAggregate, TId>>> LoadEventsAsync<TAggregate, TId>(
        TId aggregateId,
        int fromVersion = 0,
        CancellationToken cancellationToken = default)
        where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new()
    {
        if (!_streams.TryGetValue(GetStreamKeyLevel1<TAggregate>(), out var streamList))
            return Task.FromResult<IReadOnlyList<EventEnvelope<TAggregate, TId>>>([]);

        if (!streamList.TryGetValue(aggregateId!, out var stream))
            return Task.FromResult<IReadOnlyList<EventEnvelope<TAggregate, TId>>>([]);

        
        lock (stream.SyncRoot)
        {
            var result = stream.Events
                .Skip(fromVersion)
                .Cast<EventEnvelope<TAggregate, TId>>()
                .ToList()
                .AsReadOnly();

            return Task.FromResult<IReadOnlyList<EventEnvelope<TAggregate, TId>>>(result);
        }
    }

    public Task<bool> IsExistsAsync<TAggregate, TId>(
        TId id,
        CancellationToken cancellationToken = default)
    {
        if (!_streams.TryGetValue(GetStreamKeyLevel1<TAggregate>(), out var streamList))
            return Task.FromResult(false);
        
        return Task.FromResult(streamList.ContainsKey(id!));
    }

    public Task<IReadOnlyList<TId>> GetAllIdsAsync<TAggregate, TId>(
        CancellationToken cancellationToken = default)
    {
        if (!_streams.TryGetValue(GetStreamKeyLevel1<TAggregate>(), out var streamList))
            return Task.FromResult<IReadOnlyList<TId>>([]);
        
        IReadOnlyList<TId> ids = streamList.Keys
            .Select(id => (TId)id)
            .ToList();

        return Task.FromResult(ids);
    }
}