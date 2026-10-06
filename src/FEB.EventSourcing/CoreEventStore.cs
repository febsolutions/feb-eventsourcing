using System.Diagnostics;

namespace FEB.EventSourcing;

public class CoreEventStore<TAggregate, TId>(
    IEventStorePersistence persistence,
    AggregateFactory<TAggregate, TId> aggregateFactory,
    IProjectionUpdater projectionUpdater,
    IEventDispatcher? eventDispatcher,
    IOutbox? outbox,
    IEventStoreMetrics metrics)
    : IEventStore<TAggregate, TId>
    where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new()
{
    private const string Source = "eventStore";

    public async Task<TAggregate?> LoadByIdAsync(TId id, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        var events = await LoadEventsAsync(id, cancellationToken: cancellationToken);

        if (events.Count == 0)
        {
            sw.Stop();
            metrics.IncrementRead<TAggregate>(Source);
            metrics.RecordRead<TAggregate>(Source, sw.Elapsed.TotalSeconds);
            return null;
        }

        var aggregate = aggregateFactory.Create(id);
        aggregate.Replay(events.Select(e => e.Payload));

        sw.Stop();
        metrics.IncrementRead<TAggregate>(Source);
        metrics.RecordRead<TAggregate>(Source, sw.Elapsed.TotalSeconds);

        return aggregate;
    }

    public async Task SaveAsync(TAggregate aggregate, CommandContext context, CancellationToken cancellationToken = default)
    {
        if (aggregate.GetUncommittedEvents().Count == 0)
            return;
        
        var sw = Stopwatch.StartNew();

        var expectedVersion = aggregate.Version;
        var nextVersion = expectedVersion;
        
        var envelopes = new List<EventEnvelope<TAggregate, TId>>();
        
        foreach (var @event in aggregate.GetUncommittedEvents())
        {
            nextVersion++;
            
            envelopes.Add(new EventEnvelope<TAggregate, TId>(
                Payload: @event,
                Metadata: new EventMetadata<TId>
                    (
                    EventId: Guid.NewGuid(),
                    AggregateId: aggregate.Id,
                    AggregateType: typeof(TAggregate).Name,
                    Version: nextVersion,
                    OccurredAt: DateTime.UtcNow,
                    TenantId: context.TenantId,
                    UserId: context.UserId,
                    CorrelationId: context.CorrelationId,
                    CausationId: context.CommandId.ToString(),
                    Headers: context.Headers
                )
            ));
        }
        
        var outboxEnvelopes = outbox != null
            ? envelopes.Select(e => e.ToOutboxEnvelope()).ToList()
            : null;

        if (outboxEnvelopes != null
            && outbox is Outbox { Persistence: var outboxPersistence }
            && persistence is IAtomicOutboxAppend atomic
            && atomic.SupportsAtomicAppend(outboxPersistence))
        {
            // Events and envelopes in one atomic operation: a stored event always has its envelope.
            await atomic.AppendEventsWithOutboxAsync(aggregate.Id, expectedVersion, envelopes, outboxEnvelopes, outboxPersistence, cancellationToken);
        }
        else
        {
            await persistence.AppendEventsAsync(aggregate.Id, expectedVersion, envelopes, cancellationToken);

            // Enqueue right after the append and before the projections, so a failing read
            // model can never keep stored events from the outbox. Without a common
            // transaction (MongoDB without a replica set, or an outbox in another store) a
            // crash between these two writes can still lose the envelopes.
            if (outboxEnvelopes != null)
            {
                if (outbox is Outbox concreteOutbox)
                    await concreteOutbox.EnqueueManyAsync(outboxEnvelopes, cancellationToken);
                else
                    foreach (var outboxEnvelope in outboxEnvelopes)
                        await outbox!.EnqueueAsync<TAggregate, TId>(outboxEnvelope, cancellationToken);
            }
        }

        var updateContext = new ProjectionUpdateContext<TAggregate, TId>(aggregate, aggregate.GetUncommittedEvents(), context.ToProjectionContext());
        
        aggregate.Commit(nextVersion);

        await projectionUpdater.UpdateAsync(updateContext, cancellationToken);
        
        if (eventDispatcher!= null)
            await eventDispatcher.DispatchAsync(envelopes, cancellationToken);
        
        sw.Stop();
        metrics.IncrementWrite<TAggregate>(Source);
        metrics.RecordWrite<TAggregate>(Source, sw.Elapsed.TotalSeconds);
    }

    public Task<IReadOnlyList<EventEnvelope<TAggregate, TId>>> LoadEventsAsync(TId aggregateId, int fromVersion = -1, CancellationToken cancellationToken = default)
        // No separate existence check: an empty result answers the same question
        // and saves one round trip on every cold load.
        => persistence.LoadEventsAsync<TAggregate, TId>(aggregateId, fromVersion, cancellationToken);

    /// <summary>
    /// Checks whether the aggregate already has events in the store.
    /// </summary>
    public Task<bool> IsExistsAsync(TId id, CancellationToken cancellationToken = default) => persistence.IsExistsAsync<TAggregate, TId>(id, cancellationToken);

    public Task<IReadOnlyList<TId>> GetAllIdsAsync(CancellationToken cancellationToken = default) => persistence.GetAllIdsAsync<TAggregate, TId>(cancellationToken);

    public async Task RebuildProjectionsAsync(CancellationToken cancellationToken = default)
    {
        var ids = await GetAllIdsAsync(cancellationToken);

        foreach (var id in ids)
        {
            var eventEnvelopes = await persistence.LoadEventsAsync<TAggregate, TId>(id, cancellationToken: cancellationToken);

            var batches = GroupEvents(eventEnvelopes);

            var aggregate = aggregateFactory.Create(id);
            
            foreach (var batch in batches)
            {
                var events = batch.Events
                    .Select(e => e.Payload)
                    .ToList();

                aggregate.Replay(events);
                
                var first = batch.Events[0];

                var context = first.Metadata.ToProjectionContext();
                var projectionContext = new ProjectionUpdateContext<TAggregate, TId>(aggregate, events, context);

                await projectionUpdater.UpdateAsync(projectionContext, cancellationToken);
            }
        }
    }

    private static List<CommandEventBatch> GroupEvents(IReadOnlyCollection<EventEnvelope<TAggregate, TId>> events)
    {
        var orderedEvents = events
            .OrderBy(e => e.Metadata.Version)
            .ToList();
        
        var batches = new List<CommandEventBatch>();

        CommandEventBatch? current = null;

        foreach (var envelope in orderedEvents)
        {
            var commandId = envelope.Metadata.CausationId!;

            if (current == null)
            {
                current = new CommandEventBatch(
                    commandId,
                    envelope.Metadata.Version);

                batches.Add(current);
            }
            else if (current.CommandId != commandId)
            {
                // A new command starts a new batch
                current = new CommandEventBatch(
                    commandId,
                    envelope.Metadata.Version);

                batches.Add(current);
            }

            current.Events.Add(envelope);
        }

        return batches;
    }

    public async Task UpdateProjectionsAsync(TId id, CommandContext context, CancellationToken cancellationToken = default)
    {
        var aggregate = await LoadByIdAsync(id, cancellationToken);
        if (aggregate == null) 
            return;

        var projectionContext = context.ToProjectionContext();
        var updateContext = new ProjectionUpdateContext<TAggregate, TId>(aggregate, [], projectionContext);
        await projectionUpdater.UpdateAsync(updateContext, cancellationToken);
    }
    
    private sealed class CommandEventBatch(string commandId, int startVersion)
    {
        public string CommandId { get; } = commandId;

        public int StartVersion { get; } = startVersion;
        
        public List<EventEnvelope<TAggregate, TId>> Events { get; } = [];
    }
}