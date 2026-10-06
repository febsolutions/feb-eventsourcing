namespace FEB.EventSourcing;

/// <summary>
/// Single-consumer outbox delivery. Kept for backwards compatibility — new code should
/// implement <see cref="IOutboxSubscriber"/>. <c>UseOutboxDispatcher&lt;T&gt;()</c> wraps a
/// dispatcher as a subscriber named after its type.
/// </summary>
public interface IOutboxDispatcher
{
    Task DispatchAsync(
        OutboxEnvelope envelope,
        CancellationToken cancellationToken = default);
}