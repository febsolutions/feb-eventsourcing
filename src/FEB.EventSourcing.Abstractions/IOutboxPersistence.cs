namespace FEB.EventSourcing;

/// <summary>An outbox envelope together with the subscriber names that still need delivery.</summary>
public sealed record PendingOutboxEnvelope(OutboxEnvelope Envelope, IReadOnlyList<string> PendingSubscribers);

public interface IOutboxPersistence
{
    Task EnqueueAsync(
        OutboxEnvelope envelope,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Enqueues the envelopes of one save. Providers write them in a single operation
    /// where they can; the default enqueues them one by one.
    /// </summary>
    async Task EnqueueManyAsync(
        IReadOnlyCollection<OutboxEnvelope> envelopes,
        CancellationToken cancellationToken = default)
    {
        foreach (var envelope in envelopes)
            await EnqueueAsync(envelope, cancellationToken);
    }

    /// <summary>
    /// Leases up to <paramref name="maxCount"/> envelopes that have at least one open
    /// delivery for any of <paramref name="subscriberNames"/>, and returns per envelope
    /// which of those subscribers are still pending.
    /// </summary>
    Task<IReadOnlyList<PendingOutboxEnvelope>> DequeueBatchAsync(
        IReadOnlyCollection<string> subscriberNames,
        int maxCount,
        CancellationToken cancellationToken = default);

    /// <summary>Marks the delivery to one subscriber as done; completes the envelope when all known subscribers are terminal.</summary>
    Task MarkDispatchedAsync(
        OutboxEnvelope envelope,
        string subscriberName,
        IReadOnlyCollection<string> allSubscriberNames,
        CancellationToken cancellationToken = default);

    /// <summary>Records a failed delivery to one subscriber; dead-letters that subscription after MaxAttempts.</summary>
    Task MarkFailedAsync(
        OutboxEnvelope envelope,
        string subscriberName,
        Exception exception,
        IReadOnlyCollection<string> allSubscriberNames,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes completed envelopes older than the provider's configured retention.
    /// Called periodically by the <c>OutboxWorker</c>; returns the number of removed
    /// envelopes. Providers that clean up out-of-band (e.g. MongoDB via a TTL index)
    /// keep the default no-op. Dead-lettered entries are never touched.
    /// </summary>
    Task<int> CleanupCompletedAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(0);
}
