using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace FEB.EventSourcing;

/// <summary>
/// Dispatches freshly saved events to all registered <c>ISyncEventHandler&lt;TEvent&gt;</c>
/// implementations inside the save call. Only reflection metadata is cached; handler
/// instances are resolved per dispatch (they are typically scoped).
/// </summary>
public sealed class DefaultEventDispatcher(IServiceProvider serviceProvider) : IEventDispatcher
{
    private static readonly ConcurrentDictionary<Type, (Type InterfaceType, MethodInfo Method)> Cache = new();

    public async Task DispatchAsync<TAggregate, TId>(
        IReadOnlyCollection<EventEnvelope<TAggregate, TId>> events,
        CancellationToken cancellationToken = default)
        where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new()
    {
        foreach (var envelope in events)
        {
            var @event = envelope.Payload;

            var (interfaceType, method) = Cache.GetOrAdd(@event.GetType(), static et =>
            {
                var iface = typeof(ISyncEventHandler<>).MakeGenericType(et);
                return (iface, iface.GetMethod(nameof(ISyncEventHandler<IEvent>.HandleAsync))!);
            });

            var handlers = serviceProvider.GetServices(interfaceType).ToList();
            if (handlers.Count == 0)
                continue;

            var context = EventHandlingContext.Create(envelope.Metadata.AggregateId!, envelope.Metadata);

            foreach (var handler in handlers)
                await (Task)method.Invoke(handler, [@event, context, cancellationToken])!;
        }
    }
}
