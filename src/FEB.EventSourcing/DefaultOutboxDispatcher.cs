using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace FEB.EventSourcing;

/// <summary>
/// The default outbox subscriber (<c>Name = "handlers"</c>): delivers each envelope to all
/// registered <c>IASyncEventHandler&lt;TEvent&gt;</c> implementations in-process.
/// </summary>
public sealed class DefaultOutboxDispatcher(IServiceProvider serviceProvider, IAggregateIdResolver aggregateIdResolver)
    : IOutboxDispatcher, IOutboxSubscriber
{
    public const string SubscriberName = "handlers";

    private static readonly ConcurrentDictionary<Type, (Type InterfaceType, MethodInfo Method)> Cache = new();

    public string Name => SubscriberName;

    public async Task DispatchAsync(OutboxEnvelope envelope, CancellationToken cancellationToken = default)
    {
        var eventType = EventTypeNames.ResolveRequired(envelope.Payload.EventType);

        var @event = (IEvent)JsonSerializer.Deserialize(envelope.Payload.Data, eventType)!;

        var ctx = EventHandlingContext.CreateFromOutbox(
            envelope.Metadata.AggregateId,
            envelope.Metadata,
            aggregateIdResolver);

        var (interfaceType, method) = Cache.GetOrAdd(eventType, static et =>
        {
            var iface = typeof(IASyncEventHandler<>).MakeGenericType(et);
            return (iface, iface.GetMethod(nameof(IASyncEventHandler<IEvent>.HandleAsync))!);
        });

        // Resolve instances per dispatch (do not cache: handlers are registered as scoped)
        foreach (var handler in serviceProvider.GetServices(interfaceType))
        {
            await (Task)method.Invoke(handler, [@event, ctx, cancellationToken])!;
        }
    }
}
