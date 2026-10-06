using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FEB.EventSourcing;

/// <summary>
/// Default outbox pump. Leases envelopes with at least one pending delivery, hands
/// each envelope to exactly the subscribers that have not received it yet, and
/// tracks success/failure <b>per subscriber</b> — a failing subscriber never causes
/// redelivery to the others. Register via <c>es.UseOutboxWorker(...)</c>; add
/// subscribers via <c>es.UseOutboxSubscriber&lt;T&gt;()</c>. To replace the pump itself,
/// host your own <see cref="BackgroundService"/> against <see cref="IOutboxPersistence"/>.
/// </summary>
public sealed class OutboxWorker(
    IServiceScopeFactory scopeFactory,
    OutboxWorkerOptions options,
    ILogger<OutboxWorker>? logger = null) : BackgroundService
{
    private readonly ILogger<OutboxWorker> _logger = logger ?? NullLogger<OutboxWorker>.Instance;

    private DateTime _nextCleanup = DateTime.UtcNow;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessBatchAsync(stoppingToken);

                if (DateTime.UtcNow >= _nextCleanup)
                {
                    _nextCleanup = DateTime.UtcNow + options.CleanupInterval;
                    await CleanupAsync(stoppingToken);
                }

                if (processed == 0)
                    await Task.Delay(options.PollingInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // normal shutdown
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OutboxWorker loop crashed, backing off");
                await Task.Delay(options.ErrorBackoff, stoppingToken);
            }
        }
    }

    /// <summary>
    /// Processes one batch inside its own DI scope. Returns the number of leased envelopes.
    /// </summary>
    internal async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();

        var outbox = scope.ServiceProvider.GetRequiredService<IOutboxPersistence>();
        var subscribers = scope.ServiceProvider.GetServices<IOutboxSubscriber>()
            .GroupBy(s => s.Name)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var name in subscribers.Keys)
            OutboxSubscriberName.Validate(name);

        if (subscribers.Count == 0)
        {
            _logger.LogWarning("OutboxWorker has no subscribers registered — nothing to deliver");
            return 0;
        }

        var allNames = subscribers.Keys.ToList();

        var batch = await outbox.DequeueBatchAsync(allNames, options.BatchSize, cancellationToken);

        if (batch.Count == 0)
            return 0;

        foreach (var pending in batch)
        {
            foreach (var name in pending.PendingSubscribers)
            {
                if (!subscribers.TryGetValue(name, out var subscriber))
                    continue;

                try
                {
                    await subscriber.DispatchAsync(pending.Envelope, cancellationToken);
                    await outbox.MarkDispatchedAsync(pending.Envelope, name, allNames, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Outbox delivery to subscriber {Subscriber} failed for EventId={EventId}, AggregateType={AggregateType}, Version={Version}",
                        name,
                        pending.Envelope.Metadata.EventId,
                        pending.Envelope.Metadata.AggregateType,
                        pending.Envelope.Metadata.Version);

                    await outbox.MarkFailedAsync(pending.Envelope, name, ex, allNames, cancellationToken);
                }
            }
        }

        return batch.Count;
    }

    /// <summary>
    /// Retention cleanup: delegates to the persistence (SQL providers delete completed
    /// envelopes past their retention; MongoDB uses a TTL index and no-ops here).
    /// Failures are logged and never affect delivery.
    /// </summary>
    internal async Task CleanupAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var outbox = scope.ServiceProvider.GetRequiredService<IOutboxPersistence>();

            var removed = await outbox.CleanupCompletedAsync(cancellationToken);

            if (removed > 0)
                _logger.LogDebug("Outbox cleanup removed {Count} completed envelopes", removed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Outbox cleanup failed; will retry at the next interval");
        }
    }
}
