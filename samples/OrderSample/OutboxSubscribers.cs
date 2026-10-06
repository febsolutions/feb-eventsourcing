using FEB.EventSourcing;

namespace OrderSample;

/// <summary>
/// An async handler: runs via the outbox (default subscriber "handlers"), never on
/// the command path. Must be idempotent — the outbox is at-least-once.
/// </summary>
public sealed class SendShippingMailHandler : IASyncEventHandler<OrderShipped>
{
    public Task HandleAsync(OrderShipped @event, EventHandlingContext context, CancellationToken cancellationToken)
    {
        Console.WriteLine($"   -> async handler: mail for order {context.GetAggregateId<string>()} (event {context.EventId})");
        return Task.CompletedTask;
    }
}

/// <summary>
/// A custom outbox subscriber — the place where you would publish to RabbitMQ,
/// call a partner API, etc. Tracked, retried and dead-lettered independently of the
/// default "handlers" subscriber. Use EventId as the idempotency key downstream.
/// </summary>
public sealed class ConsoleBrokerPublisher : IOutboxSubscriber
{
    public string Name => "console-broker";

    public Task DispatchAsync(OutboxEnvelope envelope, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"   -> broker publish: {envelope.Payload.EventType.Split(',')[0]} " +
                          $"(aggregate {envelope.Metadata.AggregateId} v{envelope.Metadata.Version}, id {envelope.Metadata.EventId})");
        return Task.CompletedTask;
    }
}
