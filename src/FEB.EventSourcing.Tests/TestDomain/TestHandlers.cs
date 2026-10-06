using System.Collections.Concurrent;

namespace FEB.EventSourcing.Tests.TestDomain;

/// <summary>
/// Static recorder for handler/projection calls. Tests use unique aggregate data
/// (GUIDs), so concurrent use is safe.
/// </summary>
public static class EventRecorder
{
    public static ConcurrentBag<string> SyncHandled { get; } = [];
    public static ConcurrentBag<string> AsyncHandled { get; } = [];
    public static ConcurrentBag<string> ProjectedAggregates { get; } = [];
}

/// <summary>Wird von AddEventSourcing per Assembly-Scan registriert (synchroner Pfad).</summary>
public sealed class SyncOrderCreatedHandler : ISyncEventHandler<OrderCreated>
{
    public Task HandleAsync(OrderCreated @event, EventHandlingContext context, CancellationToken cancellationToken)
    {
        EventRecorder.SyncHandled.Add(@event.Customer);
        return Task.CompletedTask;
    }
}

/// <summary>Wird von AddEventSourcing per Assembly-Scan registriert (Outbox-Pfad).</summary>
public sealed class AsyncOrderCreatedHandler : IASyncEventHandler<OrderCreated>
{
    public Task HandleAsync(OrderCreated @event, EventHandlingContext context, CancellationToken cancellationToken)
    {
        EventRecorder.AsyncHandled.Add(@event.Customer);
        return Task.CompletedTask;
    }
}

/// <summary>Wird von AddEventSourcing per Assembly-Scan registriert (Projection-Pfad).</summary>
public sealed class OrderProjectionWriter : IAggregateProjectionWriter<Order>
{
    public Task UpdateAsync(Order aggregate, ProjectionContext context, CancellationToken cancellationToken)
    {
        EventRecorder.ProjectedAggregates.Add(aggregate.Id);
        return Task.CompletedTask;
    }
}

/// <summary>Outbox path for an event with a stable storage name ([EventName]).</summary>
public sealed class AsyncOrderRenamedHandler : IASyncEventHandler<OrderRenamed>
{
    public Task HandleAsync(OrderRenamed @event, EventHandlingContext context, CancellationToken cancellationToken)
    {
        EventRecorder.AsyncHandled.Add(@event.Customer);
        return Task.CompletedTask;
    }
}
