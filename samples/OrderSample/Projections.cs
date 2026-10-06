using System.Collections.Concurrent;
using FEB.EventSourcing;

namespace OrderSample;

/// <summary>A trivial in-memory read model. In a real app this would be a MongoDB collection.</summary>
public sealed class OrderOverview
{
    public ConcurrentDictionary<string, string> Rows { get; } = new();
}

/// <summary>
/// Projection writers are discovered by AddEventSourcing's assembly scan and run
/// after every successful save with the up-to-date aggregate.
/// </summary>
public sealed class OrderOverviewProjection(OrderOverview overview) : IAggregateProjectionWriter<Order>
{
    public Task UpdateAsync(Order aggregate, ProjectionContext context, CancellationToken cancellationToken)
    {
        overview.Rows[aggregate.Id] =
            $"{aggregate.Customer,-12} {aggregate.Status,-8} {aggregate.Lines.Count} lines, total {aggregate.Total:0.00}";
        return Task.CompletedTask;
    }
}

/// <summary>
/// Sync handlers run inside the save call (same process). Use them for cheap,
/// same-transactional-scope reactions; use IASyncEventHandler + the outbox for
/// anything that must not block or fail the command path.
/// </summary>
public sealed class OrderShippedHandler : ISyncEventHandler<OrderShipped>
{
    public Task HandleAsync(OrderShipped @event, EventHandlingContext context, CancellationToken cancellationToken)
    {
        Console.WriteLine($"   -> sync handler: order {context.GetAggregateId<string>()} was shipped");
        return Task.CompletedTask;
    }
}
