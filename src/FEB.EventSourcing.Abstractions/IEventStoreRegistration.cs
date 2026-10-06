using Microsoft.Extensions.DependencyInjection;

namespace FEB.EventSourcing;

/// <summary>
/// Well-known positions in the event store decorator chain. Lower values are closer
/// to the core store (innermost); higher values wrap the ones below. Custom layers
/// pick any value between the well-known ones.
/// </summary>
public static class EventStoreLayer
{
    /// <summary>The core store (replay/append). Always innermost.</summary>
    public const int Core = 0;

    /// <summary>Persistent snapshots — must sit directly on the core so caches above see snapshot-accelerated loads.</summary>
    public const int Snapshots = 100;

    /// <summary>State caches (e.g. Redis) — outside snapshots so cache hits skip persistence entirely.</summary>
    public const int Cache = 200;

    /// <summary>Cross-cutting concerns (metrics, tracing, auditing) — outermost by default.</summary>
    public const int CrossCutting = 300;
}

public interface IEventStoreRegistration
{
    /// <summary>
    /// Position in the decorator chain (see <see cref="EventStoreLayer"/>). Registrations
    /// are sorted ascending; equal values keep registration order.
    /// </summary>
    int Order { get; }

    IEventStore<TAggregate, TId> CreateEventStore<TAggregate, TId>(
        IEventStore<TAggregate, TId>? innerStore,
        IServiceProvider sp,
        EventStoreOptions eventStoreOptions)
        where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new();

    Type GetRegisteredType<TAggregate, TId>()
        where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new();

    void RegisterEventStore<TAggregate, TId>(
        IServiceCollection services,
        Type? innerStoreType,
        EventStoreOptions eventStoreOptions,
        bool registerAsOuterStore = false)
        where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new();
}
