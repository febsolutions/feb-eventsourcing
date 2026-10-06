namespace FEB.EventSourcing;

/// <summary>
/// A named consumer of outbox events. Delivery, retries and dead-lettering are tracked
/// per subscriber, so one failing consumer never causes redelivery to the others.
/// Typical subscribers: the in-process handler dispatcher (default), a message broker
/// publisher, a third-party API client. Implementations must be idempotent
/// (at-least-once delivery).
/// </summary>
public interface IOutboxSubscriber
{
    /// <summary>
    /// Stable, unique name — the key under which deliveries are tracked. Renaming a
    /// subscriber makes it a new subscriber (it will only see new envelopes).
    /// </summary>
    string Name { get; }

    Task DispatchAsync(OutboxEnvelope envelope, CancellationToken cancellationToken = default);
}
