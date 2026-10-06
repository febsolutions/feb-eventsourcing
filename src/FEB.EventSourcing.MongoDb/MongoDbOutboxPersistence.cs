using System.Text.Json;
using MongoDB.Driver;

namespace FEB.EventSourcing.MongoDb;

/// <summary>
/// MongoDB outbox with per-subscriber delivery tracking. Dequeue leases whole
/// envelopes; delivery state, retries and dead-lettering are kept per subscriber
/// name in <see cref="OutboxDao.Deliveries"/>. Envelopes written before subscription
/// tracking existed (no <c>Deliveries</c>) are treated as pending for every subscriber.
/// </summary>
public class MongoDbOutboxPersistence(
    IMongoDbContext dbContext,
    MongoOutboxOptions options
) : IOutboxPersistence
{
    /// <summary>Name of the outbox collection; the event persistence writes to it inside its append transaction.</summary>
    internal string CollectionName => options.OutboxCollectionName;

    public async Task EnqueueAsync(
        OutboxEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        await GetOutboxCollection()
            .InsertOneAsync(CreateDao(envelope), cancellationToken: cancellationToken);
    }

    public async Task EnqueueManyAsync(
        IReadOnlyCollection<OutboxEnvelope> envelopes,
        CancellationToken cancellationToken = default)
    {
        if (envelopes.Count == 0)
            return;

        await GetOutboxCollection()
            .InsertManyAsync(envelopes.Select(CreateDao), new InsertManyOptions { IsOrdered = true }, cancellationToken);
    }

    internal static OutboxDao CreateDao(OutboxEnvelope envelope) => new()
    {
        Id = envelope.Metadata.EventId,
        PayloadJson = JsonSerializer.Serialize(envelope.Payload),
        MetadataJson = JsonSerializer.Serialize(envelope.Metadata),
        CreatedAt = DateTime.UtcNow,
        Deliveries = new Dictionary<string, OutboxDeliveryDao>()
    };

    public async Task<IReadOnlyList<PendingOutboxEnvelope>> DequeueBatchAsync(
        IReadOnlyCollection<string> subscriberNames,
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        var collection = GetOutboxCollection();
        var now = DateTime.UtcNow;

        var result = new List<PendingOutboxEnvelope>();

        // Coarse filter: not completed + not leased. Whether a specific subscriber
        // is still pending is decided in memory (dictionary keys are dynamic).
        var filter = Builders<OutboxDao>.Filter.And(
            Builders<OutboxDao>.Filter.Eq(x => x.DispatchedAt, null),
            Builders<OutboxDao>.Filter.Or(
                Builders<OutboxDao>.Filter.Eq(x => x.LockedUntil, null),
                Builders<OutboxDao>.Filter.Lt(x => x.LockedUntil, now)
            )
        );

        var update = Builders<OutboxDao>.Update
            .Set(x => x.LockedUntil, now.Add(options.LeaseDuration));

        var findOptions = new FindOneAndUpdateOptions<OutboxDao>
        {
            Sort = Builders<OutboxDao>.Sort.Ascending(x => x.CreatedAt),
            ReturnDocument = ReturnDocument.After
        };

        // Lease one at a time (atomic per document) until the batch is full or the
        // queue is drained. Envelopes with nothing pending for our subscribers are
        // completed if all known subscribers are terminal, else released.
        for (var i = 0; i < maxCount * 2 && result.Count < maxCount; i++)
        {
            var dao = await collection.FindOneAndUpdateAsync(filter, update, findOptions, cancellationToken);
            if (dao == null)
                break;

            var pending = subscriberNames.Where(n => !IsTerminal(dao, n)).ToList();

            if (pending.Count > 0)
            {
                result.Add(new PendingOutboxEnvelope(MapToEnvelope(dao), pending));
                continue;
            }

            // Nothing left for us: complete it if every known subscriber is terminal,
            // otherwise release the lease so a worker with other subscribers can take it.
            if (subscriberNames.All(n => IsTerminal(dao, n)))
                await CompleteEnvelopeAsync(collection, dao.Id, cancellationToken);
            else
                await ReleaseLeaseAsync(collection, dao.Id, cancellationToken);
        }

        return result;
    }

    public async Task MarkDispatchedAsync(
        OutboxEnvelope envelope,
        string subscriberName,
        IReadOnlyCollection<string> allSubscriberNames,
        CancellationToken cancellationToken = default)
    {
        var collection = GetOutboxCollection();
        var id = envelope.Metadata.EventId;

        var update = Builders<OutboxDao>.Update
            .Set($"Deliveries.{subscriberName}.DispatchedAt", DateTime.UtcNow)
            .Unset($"Deliveries.{subscriberName}.LastError");

        var updated = await collection.FindOneAndUpdateAsync(
            Builders<OutboxDao>.Filter.Eq(x => x.Id, id),
            update,
            new FindOneAndUpdateOptions<OutboxDao> { ReturnDocument = ReturnDocument.After },
            cancellationToken);

        if (updated != null && allSubscriberNames.All(n => IsTerminal(updated, n)))
            await CompleteEnvelopeAsync(collection, id, cancellationToken);
    }

    public async Task MarkFailedAsync(
        OutboxEnvelope envelope,
        string subscriberName,
        Exception exception,
        IReadOnlyCollection<string> allSubscriberNames,
        CancellationToken cancellationToken = default)
    {
        var outbox = GetOutboxCollection();
        var id = envelope.Metadata.EventId;

        var update = Builders<OutboxDao>.Update
            .Inc($"Deliveries.{subscriberName}.AttemptCount", 1)
            .Set($"Deliveries.{subscriberName}.LastError", exception.ToString());

        var updated = await outbox.FindOneAndUpdateAsync(
            Builders<OutboxDao>.Filter.Eq(x => x.Id, id),
            update,
            new FindOneAndUpdateOptions<OutboxDao> { ReturnDocument = ReturnDocument.After },
            cancellationToken);

        if (updated == null)
            return; // already gone – idempotent

        var delivery = updated.Deliveries?[subscriberName];
        if (delivery == null || delivery.AttemptCount < options.MaxAttempts)
        {
            // Release the lease so the next poll retries this envelope
            await ReleaseLeaseAsync(outbox, id, cancellationToken);
            return;
        }

        // Dead-letter this subscription only
        var dlq = new OutboxDeadLetterDao
        {
            Id = $"{id}:{subscriberName}",
            EventId = id,
            Subscriber = subscriberName,
            PayloadJson = updated.PayloadJson,
            MetadataJson = updated.MetadataJson,
            CreatedAt = updated.CreatedAt,
            AttemptCount = delivery.AttemptCount,
            FailedAt = DateTime.UtcNow,
            LastError = delivery.LastError ?? "Unknown error"
        };

        try
        {
            await GetDeadLetterCollection().InsertOneAsync(dlq, cancellationToken: cancellationToken);
        }
        catch (MongoWriteException mwx) when (mwx.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // already dead-lettered
        }

        var terminal = await outbox.FindOneAndUpdateAsync(
            Builders<OutboxDao>.Filter.Eq(x => x.Id, id),
            Builders<OutboxDao>.Update
                .Set($"Deliveries.{subscriberName}.DeadLetteredAt", DateTime.UtcNow)
                .Set(x => x.LockedUntil, null),
            new FindOneAndUpdateOptions<OutboxDao> { ReturnDocument = ReturnDocument.After },
            cancellationToken);

        if (terminal != null && allSubscriberNames.All(n => IsTerminal(terminal, n)))
            await CompleteEnvelopeAsync(outbox, id, cancellationToken);
    }

    /// <summary>
    /// Creates the dequeue index (idempotent). Called by <see cref="MongoDbIndexInitializer"/>
    /// at startup. Keys are (DispatchedAt, CreatedAt): the dequeue query filters
    /// DispatchedAt = null with an $or on LockedUntil and sorts by CreatedAt — with
    /// LockedUntil in the middle of the index the $or breaks the index-backed sort and
    /// every lease degrades to a blocking sort over the whole backlog, which gets slower
    /// with every pending envelope. With (DispatchedAt, CreatedAt)
    /// the index serves equality + sort and LockedUntil is filtered residually.
    /// Migrates automatically: an existing "ix_dequeue" with the old key spec is dropped.
    /// </summary>
    public async Task EnsureIndexesAsync(CancellationToken cancellationToken = default)
    {
        var indexes = GetOutboxCollection().Indexes;

        var model = new CreateIndexModel<OutboxDao>(
            Builders<OutboxDao>.IndexKeys
                .Ascending(x => x.DispatchedAt)
                .Ascending(x => x.CreatedAt),
            new CreateIndexOptions { Name = "ix_dequeue" });

        try
        {
            await indexes.CreateOneAsync(model, cancellationToken: cancellationToken);
        }
        catch (MongoCommandException ex) when (ex.CodeName == "IndexKeySpecsConflict" || ex.Code == 86)
        {
            // same name, old key spec (pre-9.0.0-beta.2) → replace
            await indexes.DropOneAsync("ix_dequeue", cancellationToken);
            await indexes.CreateOneAsync(model, cancellationToken: cancellationToken);
        }

        // Retention: MongoDB removes completed envelopes itself via a TTL index on
        // DispatchedAt (pending envelopes have DispatchedAt = null, which TTL ignores).
        // A changed retention (same name, different expireAfterSeconds) is migrated;
        // disabling drops the index.
        if (options.CompletedRetention is { } retention)
        {
            var ttlModel = new CreateIndexModel<OutboxDao>(
                Builders<OutboxDao>.IndexKeys.Ascending(x => x.DispatchedAt),
                new CreateIndexOptions { Name = "ttl_dispatched", ExpireAfter = retention });

            try
            {
                await indexes.CreateOneAsync(ttlModel, cancellationToken: cancellationToken);
            }
            catch (MongoCommandException ex) when (ex.Code is 85 or 86)
            {
                // IndexOptionsConflict / IndexKeySpecsConflict → retention changed
                await indexes.DropOneAsync("ttl_dispatched", cancellationToken);
                await indexes.CreateOneAsync(ttlModel, cancellationToken: cancellationToken);
            }
        }
        else
        {
            try
            {
                await indexes.DropOneAsync("ttl_dispatched", cancellationToken);
            }
            catch (MongoCommandException)
            {
                // did not exist – fine
            }
        }
    }

    private static bool IsTerminal(OutboxDao dao, string subscriberName)
    {
        // Legacy envelope without per-subscriber tracking: pending for everyone until completed.
        if (dao.Deliveries == null)
            return dao.DispatchedAt != null;

        return dao.Deliveries.TryGetValue(subscriberName, out var d) && d.IsTerminal;
    }

    private static Task CompleteEnvelopeAsync(IMongoCollection<OutboxDao> collection, Guid id, CancellationToken ct)
        => collection.UpdateOneAsync(
            Builders<OutboxDao>.Filter.Eq(x => x.Id, id),
            Builders<OutboxDao>.Update
                .Set(x => x.DispatchedAt, DateTime.UtcNow)
                .Set(x => x.LockedUntil, null),
            cancellationToken: ct);

    private static Task ReleaseLeaseAsync(IMongoCollection<OutboxDao> collection, Guid id, CancellationToken ct)
        => collection.UpdateOneAsync(
            Builders<OutboxDao>.Filter.Eq(x => x.Id, id),
            Builders<OutboxDao>.Update.Set(x => x.LockedUntil, null),
            cancellationToken: ct);

    private static OutboxEnvelope MapToEnvelope(OutboxDao dao)
    {
        var payload = JsonSerializer.Deserialize<OutboxPayload>(dao.PayloadJson)!;
        var metadata = JsonSerializer.Deserialize<OutboxMetadata>(dao.MetadataJson)!;

        return new OutboxEnvelope(payload, metadata);
    }

    private IMongoCollection<OutboxDao> GetOutboxCollection() =>
        dbContext.GetCollection<OutboxDao>(options.OutboxCollectionName);

    private IMongoCollection<OutboxDeadLetterDao> GetDeadLetterCollection()
        => dbContext.GetCollection<OutboxDeadLetterDao>(options.DeadLetterCollectionName);
}
