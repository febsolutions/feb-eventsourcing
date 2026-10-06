using System.Collections.Concurrent;
using FEB.EventSourcing.Redis;

namespace FEB.EventSourcing.Tests.Redis;

/// <summary>
/// In-memory stand-in for <see cref="IRedisCacheDatabase"/> including the CAS semantics
/// of the Lua script (only set when the new stream version is higher).
/// </summary>
public sealed class FakeRedisCacheDatabase : IRedisCacheDatabase
{
    public ConcurrentDictionary<string, CachedAggregateState> Store { get; } = new();

    public Task<CachedAggregateState?> GetAsync(string key)
        => Task.FromResult(Store.TryGetValue(key, out var state) ? state : null);

    public Task<bool> TrySetAsync(string key, CachedAggregateState state, TimeSpan ttl)
    {
        var written = false;

        Store.AddOrUpdate(key,
            _ => { written = true; return state; },
            (_, existing) =>
            {
                if (existing.StreamVersion >= state.StreamVersion)
                    return existing;

                written = true;
                return state;
            });

        return Task.FromResult(written);
    }

    public Task<bool> ExistsAsync(string key) => Task.FromResult(Store.ContainsKey(key));

    public Task DeleteAsync(string key)
    {
        Store.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}

/// <summary>Counting inner store: returns a fixed aggregate state and records accesses.</summary>
public sealed class CountingInnerStore<TAggregate, TId>(Func<TId, TAggregate?> loader) : IEventStore<TAggregate, TId>
    where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new()
{
    public int LoadCalls { get; private set; }
    public int SaveCalls { get; private set; }
    public Exception? ThrowOnSave { get; set; }

    public Task<TAggregate?> LoadByIdAsync(TId id, CancellationToken cancellationToken = default)
    {
        LoadCalls++;
        return Task.FromResult(loader(id));
    }

    public Task SaveAsync(TAggregate aggregate, CommandContext context, CancellationToken cancellationToken = default)
    {
        SaveCalls++;

        if (ThrowOnSave != null)
            throw ThrowOnSave;

        var newVersion = aggregate.Version + aggregate.GetUncommittedEvents().Count;
        aggregate.Commit(newVersion);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<EventEnvelope<TAggregate, TId>>> LoadEventsAsync(TId aggregateId, int fromVersion = -1, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<EventEnvelope<TAggregate, TId>>>([]);

    public Task<bool> IsExistsAsync(TId id, CancellationToken cancellationToken = default)
        => Task.FromResult(loader(id) != null);

    public Task<IReadOnlyList<TId>> GetAllIdsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<TId>>([]);

    public Task RebuildProjectionsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task UpdateProjectionsAsync(TId id, CommandContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>Records metrics calls per source.</summary>
public sealed class RecordingMetrics : IEventStoreMetrics
{
    public List<string> Reads { get; } = [];
    public List<string> Writes { get; } = [];

    public void RecordRead<T>(string source, double durationSeconds) { }
    public void RecordRead(Type type, string source, double durationSeconds) { }
    public void RecordWrite<T>(string source, double durationSeconds) { }
    public void RecordWrite(Type type, string source, double durationSeconds) { }
    public void IncrementRead<T>(string source) => Reads.Add(source);
    public void IncrementRead(Type type, string source) => Reads.Add(source);
    public void IncrementWrite<T>(string source) => Writes.Add(source);
    public void IncrementWrite(Type type, string source) => Writes.Add(source);
}
