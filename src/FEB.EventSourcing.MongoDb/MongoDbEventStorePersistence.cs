using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Linq;

namespace FEB.EventSourcing.MongoDb;

/// <summary>
/// MongoDB event persistence. Works on standalone servers (no replica set / no
/// multi-document transactions required):
/// <list type="number">
///   <item>The per-aggregate version document is advanced with an atomic
///   compare-and-set — this is the single optimistic-concurrency anchor.</item>
///   <item>Events are inserted afterwards; a unique index on
///   (AggregateId, Version) makes the insert idempotent.</item>
///   <item>If the two steps diverge (crash, network error, election), the stream
///   is self-healing on the next full load: the version document is reconciled
///   to the events actually stored — in both directions, because stored events
///   are the truth (a write the server committed has happened, even if the
///   client saw an error).</item>
/// </list>
/// With <see cref="MongoEventStoreOptions.UseTransactions"/> (requires a replica
/// set; a single-node replica set is sufficient) version check and event insert
/// run in one multi-document transaction instead — no intermediate states exist.
/// </summary>
public class MongoDbEventStorePersistence(
    IMongoDbContext dbContext,
    EventStoreCollectionNameBuilder collectionNameBuilder,
    IEventStoreMetrics? mayBeMetrics,
    ILogger<MongoDbEventStorePersistence>? logger = null,
    MongoEventStoreOptions? options = null)
    : IEventStorePersistence, IAtomicOutboxAppend
{
    const string Source = "mongodb-events";

    private readonly IEventStoreMetrics _metrics = mayBeMetrics ?? new NoOpEventStoreMetrics();
    private readonly ILogger _logger = logger ?? NullLogger<MongoDbEventStorePersistence>.Instance;
    private readonly bool _transactional = options?.TransactionsEnabled ?? false;

    public async Task AppendEventsAsync<TAggregate, TId>(TId id, int expectedVersion, ICollection<EventEnvelope<TAggregate, TId>> events, CancellationToken cancellationToken = default)
        where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new()
    {
        if (events.Count == 0)
            return;

        var sw = Stopwatch.StartNew();

        var lastVersion = expectedVersion + events.Count;
        var daos = events.Select(CreateDao).ToList();

        if (_transactional)
            await AppendTransactionalAsync<TAggregate, TId>(id, expectedVersion, lastVersion, daos, null, cancellationToken);
        else
            await AppendTwoStepAsync<TAggregate, TId>(id, expectedVersion, lastVersion, daos, cancellationToken);

        sw.Stop();
        _metrics.IncrementWrite<TAggregate>(Source);
        _metrics.RecordWrite<TAggregate>(Source, sw.Elapsed.TotalSeconds);
    }

    /// <summary>
    /// Atomic with the events only in transactional mode (<c>UseTransactions()</c>, replica
    /// set) and with the MongoDB outbox. Without transactions two collections cannot be
    /// written atomically; the event store then enqueues right after the append.
    /// </summary>
    public bool SupportsAtomicAppend(IOutboxPersistence outbox)
        => _transactional && outbox is MongoDbOutboxPersistence;

    public async Task AppendEventsWithOutboxAsync<TAggregate, TId>(TId id, int expectedVersion, ICollection<EventEnvelope<TAggregate, TId>> events,
        IReadOnlyCollection<OutboxEnvelope> outboxEnvelopes, IOutboxPersistence outbox, CancellationToken cancellationToken = default)
        where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new()
    {
        if (!SupportsAtomicAppend(outbox))
            throw new InvalidOperationException("Atomic outbox appends require UseTransactions() and the MongoDB outbox (UseMongoOutbox).");

        if (events.Count == 0)
            return;

        var sw = Stopwatch.StartNew();
        var lastVersion = expectedVersion + events.Count;
        var daos = events.Select(CreateDao).ToList();
        var outboxDaos = outboxEnvelopes.Select(MongoDbOutboxPersistence.CreateDao).ToList();

        await AppendTransactionalAsync<TAggregate, TId>(id, expectedVersion, lastVersion, daos,
            (((MongoDbOutboxPersistence)outbox).CollectionName, outboxDaos), cancellationToken);

        sw.Stop();
        _metrics.IncrementWrite<TAggregate>(Source);
        _metrics.RecordWrite<TAggregate>(Source, sw.Elapsed.TotalSeconds);
    }

    private async Task AppendTwoStepAsync<TAggregate, TId>(TId id, int expectedVersion, int lastVersion, List<EventEnvelopeDao<TId>> daos, CancellationToken cancellationToken)
        where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new()
    {
        // 1) Atomic version check + advance (optimistic concurrency anchor)
        await TryAdvanceVersionAsync<TAggregate, TId>(id, expectedVersion, lastVersion, session: null, cancellationToken);

        // 2) Insert events. The unique (AggregateId, Version) index detects both a
        //    genuine retry (same events already present) and a foreign orphan.
        var eventsCollection = GetEventCollection<TAggregate, TId>();

        try
        {
            await eventsCollection.InsertManyAsync(daos, new InsertManyOptions { IsOrdered = true }, cancellationToken);
        }
        catch (MongoBulkWriteException<EventEnvelopeDao<TId>> ex)
            when (ex.WriteErrors.All(e => e.Category == ServerErrorCategory.DuplicateKey))
        {
            // A document already exists at one of our versions. That is only fine if
            // it IS our event (retry after a crash between the two steps). It must
            // never be treated as success blindly: if a foreign event occupies the
            // version, acknowledging would silently discard the new event.
            await ReconcileDuplicateInsertAsync<TAggregate, TId>(id, expectedVersion, lastVersion, daos, cancellationToken);
        }
        catch (Exception)
        {
            // Insert failed after the version was advanced: roll the version back so
            // the stream is immediately consistent again (best effort; the self-heal
            // on load covers the case where even this fails — or where the insert
            // was in fact committed server-side and only the response was lost).
            await TryRollbackVersionAsync<TAggregate, TId>(id, lastVersion, expectedVersion, cancellationToken);
            throw;
        }
    }

    private async Task AppendTransactionalAsync<TAggregate, TId>(TId id, int expectedVersion, int lastVersion, List<EventEnvelopeDao<TId>> daos,
        (string Collection, List<OutboxDao> Documents)? outbox, CancellationToken cancellationToken)
        where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new()
    {
        using var session = await dbContext.Client.StartSessionAsync(cancellationToken: cancellationToken);
        session.StartTransaction();

        try
        {
            await TryAdvanceVersionAsync<TAggregate, TId>(id, expectedVersion, lastVersion, session, cancellationToken);

            await GetEventCollection<TAggregate, TId>()
                .InsertManyAsync(session, daos, new InsertManyOptions { IsOrdered = true }, cancellationToken);

            // Outbox envelopes in the same transaction. The collection comes from this
            // persistence's own context: a session is only valid on the client that started it.
            if (outbox is { Documents.Count: > 0 } o)
                await dbContext.GetCollection<OutboxDao>(o.Collection)
                    .InsertManyAsync(session, o.Documents, new InsertManyOptions { IsOrdered = true }, cancellationToken);

            await session.CommitTransactionAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            await TryAbortAsync(session);

            // Nothing was written — map the conflict shapes onto the one exception
            // the caller knows. A duplicate key here means a foreign event already
            // occupies one of our versions (legacy orphan); a WriteConflict means a
            // concurrent transaction touched the version document first.
            if (ex is MongoBulkWriteException<EventEnvelopeDao<TId>> bulk
                && bulk.WriteErrors.All(e => e.Category == ServerErrorCategory.DuplicateKey))
                throw new ConcurrencyException<TId>(id, expectedVersion,
                    await ReadLastStoredVersionAsync<TAggregate, TId>(id, cancellationToken), ex);

            if (ex is MongoException mongoEx && mongoEx.HasErrorLabel("TransientTransactionError"))
                throw new ConcurrencyException<TId>(id, expectedVersion, innerException: ex);

            throw;
        }
    }

    private async Task TryAbortAsync(IClientSessionHandle session)
    {
        try
        {
            if (session.IsInTransaction)
                await session.AbortTransactionAsync();
        }
        catch (Exception ex)
        {
            // The server aborts an uncommitted transaction on its own; this is cleanup.
            _logger.LogDebug(ex, "Aborting append transaction failed (server-side timeout will clean up)");
        }
    }

    /// <summary>
    /// Called when an insert ran into duplicate keys after the version advance:
    /// verifies per version that the stored document is OUR event (idempotent
    /// retry) and inserts whatever is still missing; a foreign document at one of
    /// our versions is a genuine conflict.
    /// </summary>
    private async Task ReconcileDuplicateInsertAsync<TAggregate, TId>(TId id, int expectedVersion, int lastVersion, List<EventEnvelopeDao<TId>> daos, CancellationToken cancellationToken)
        where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new()
    {
        var eventsCollection = GetEventCollection<TAggregate, TId>();

        var range = Builders<EventEnvelopeDao<TId>>.Filter.And(
            Builders<EventEnvelopeDao<TId>>.Filter.Eq(x => x.Metadata.AggregateId, id),
            Builders<EventEnvelopeDao<TId>>.Filter.Gte(x => x.Metadata.Version, expectedVersion + 1),
            Builders<EventEnvelopeDao<TId>>.Filter.Lte(x => x.Metadata.Version, lastVersion));

        var stored = await eventsCollection.Find(range).ToListAsync(cancellationToken);
        var storedByVersion = stored.ToDictionary(x => x.Metadata.Version, x => x.Metadata.EventId);

        var missing = new List<EventEnvelopeDao<TId>>();
        foreach (var dao in daos)
        {
            if (!storedByVersion.TryGetValue(dao.Metadata.Version, out var storedEventId))
            {
                missing.Add(dao);
                continue;
            }

            if (storedEventId != dao.Metadata.EventId)
            {
                // Foreign event at our version (orphan of an earlier append whose
                // rollback fired although the insert had committed). Stored events
                // are the truth — reject this append; the next full load reconciles
                // the version document via self-heal.
                _logger.LogWarning(
                    "Append conflict for {AggregateType} {Id}: version {Version} is already occupied by foreign event {StoredEventId}",
                    typeof(TAggregate).Name, id, dao.Metadata.Version, storedEventId);

                throw new ConcurrencyException<TId>(id, expectedVersion,
                    await ReadLastStoredVersionAsync<TAggregate, TId>(id, cancellationToken));
            }
        }

        if (missing.Count > 0)
            await eventsCollection.InsertManyAsync(missing, new InsertManyOptions { IsOrdered = true }, cancellationToken);
    }

    private async Task<int?> ReadLastStoredVersionAsync<TAggregate, TId>(TId id, CancellationToken cancellationToken)
        where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new()
    {
        var last = await GetEventCollection<TAggregate, TId>()
            .Find(Builders<EventEnvelopeDao<TId>>.Filter.Eq(x => x.Metadata.AggregateId, id))
            .SortByDescending(x => x.Metadata.Version)
            .Limit(1)
            .FirstOrDefaultAsync(cancellationToken);

        return last?.Metadata.Version;
    }

    public async Task<IReadOnlyList<EventEnvelope<TAggregate, TId>>> LoadEventsAsync<TAggregate, TId>(
            TId aggregateId,
            int fromVersion = 0,
            CancellationToken cancellationToken = default)
        where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new()
    {
        var sw = Stopwatch.StartNew();

        var collection = GetEventCollection<TAggregate, TId>();

        var filter = Builders<EventEnvelopeDao<TId>>.Filter.And(
            Builders<EventEnvelopeDao<TId>>.Filter.Eq(x => x.Metadata.AggregateId, aggregateId),
            Builders<EventEnvelopeDao<TId>>.Filter.Gte(x => x.Metadata.Version, fromVersion)
        );

        var daos = await collection
            .Find(filter)
            .SortBy(x => x.Metadata.Version)
            .ToListAsync(cancellationToken);

        var eventEnvelopes = daos.Select(dao => new EventEnvelope<TAggregate, TId>(
            DeserializeFromBson<IDomainEvent<TAggregate>>(dao.Payload),
            Map(dao.Metadata)
        )).ToList();

        await HealVersionMismatchAsync<TAggregate, TId>(aggregateId, fromVersion, eventEnvelopes, cancellationToken);

        sw.Stop();

        _metrics.IncrementRead<TAggregate>(Source);
        _metrics.RecordRead<TAggregate>(Source, sw.Elapsed.TotalSeconds);

        return eventEnvelopes;
    }

    public async Task<bool> IsExistsAsync<TAggregate, TId>(TId id, CancellationToken cancellationToken = default)
    {
        var collection = GetVersionCollection<TAggregate, TId>();

        return await collection
            .Find(x => x.Id!.Equals(id))
            .Limit(1)
            .AnyAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TId>> GetAllIdsAsync<TAggregate, TId>(CancellationToken cancellationToken = default)
    {
        var collection = GetVersionCollection<TAggregate, TId>();
        return await collection
            .AsQueryable()
            .Select(e => e.Id)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Creates the indexes the store relies on (idempotent). Called by
    /// <c>EnsureEventStoreIndexesAsync</c> at startup for every registered aggregate.
    /// </summary>
    public async Task EnsureIndexesAsync<TAggregate, TId>(CancellationToken cancellationToken = default)
        where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new()
    {
        var events = GetEventCollection<TAggregate, TId>();

        var keys = Builders<EventEnvelopeDao<TId>>.IndexKeys
            .Ascending(x => x.Metadata.AggregateId)
            .Ascending(x => x.Metadata.Version);

        await events.Indexes.CreateOneAsync(
            new CreateIndexModel<EventEnvelopeDao<TId>>(keys, new CreateIndexOptions
            {
                Unique = true,
                Name = "ux_aggregate_version"
            }),
            cancellationToken: cancellationToken);
    }

    private static EventEnvelopeDao<TId> CreateDao<TAggregate, TId>(EventEnvelope<TAggregate, TId> eventData)
        where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new() =>
        new(eventData.Payload.ToBsonDocument(), MapToDao(eventData.Metadata));

    private static EventMetadataDao<TId> MapToDao<TId>(EventMetadata<TId> source)
    {
        return new EventMetadataDao<TId>(
            source.EventId,
            source.AggregateId,
            source.AggregateType,
            source.Version,
            source.OccurredAt,
            source.TenantId,
            source.UserId,
            source.CorrelationId,
            source.CausationId,
            source.Headers);
    }

    private static EventMetadata<TId> Map<TId>(EventMetadataDao<TId> source)
    {
        return new EventMetadata<TId>(
            source.EventId,
            source.AggregateId,
            source.AggregateType,
            source.Version,
            source.OccurredAt,
            source.TenantId,
            source.UserId,
            source.CorrelationId,
            source.CausationId,
            source.Headers);
    }

    private IMongoCollection<EventEnvelopeDao<TId>> GetEventCollection<TAggregate, TId>() => dbContext.GetCollection<EventEnvelopeDao<TId>>(collectionNameBuilder.GetEventStoreName<TAggregate>());

    private IMongoCollection<AggregateVersionDao<TId>> GetVersionCollection<TAggregate, TId>() => dbContext.GetCollection<AggregateVersionDao<TId>>(collectionNameBuilder.GetAggregateVersionsStoreName<TAggregate>());

    private async Task TryAdvanceVersionAsync<TAggregate, TId>(
        TId id,
        int expectedVersion,
        int newVersion,
        IClientSessionHandle? session,
        CancellationToken cancellationToken)
    {
        var filter = Builders<AggregateVersionDao<TId>>.Filter.And(
            Builders<AggregateVersionDao<TId>>.Filter.Eq(x => x.Id, id),
            Builders<AggregateVersionDao<TId>>.Filter.Eq(x => x.Version, expectedVersion)
        );

        var update = Builders<AggregateVersionDao<TId>>.Update
            .Set(x => x.Version, newVersion);

        var findOneAndUpdateOptions = new FindOneAndUpdateOptions<AggregateVersionDao<TId>>
        {
            IsUpsert = expectedVersion == -1,
            ReturnDocument = ReturnDocument.After
        };

        var versionsCollection = GetVersionCollection<TAggregate, TId>();

        AggregateVersionDao<TId>? result;
        try
        {
            result = session == null
                ? await versionsCollection.FindOneAndUpdateAsync(filter, update, findOneAndUpdateOptions, cancellationToken)
                : await versionsCollection.FindOneAndUpdateAsync(session, filter, update, findOneAndUpdateOptions, cancellationToken);
        }
        catch (MongoCommandException ex) when (ex.Code == 11000)
        {
            // Upsert race for a brand-new aggregate: two writers with expected -1,
            // the second one hits the _id duplicate → concurrency conflict.
            throw new ConcurrencyException<TId>(id, expectedVersion, innerException: ex);
        }

        if (result == null)
        {
            // Read the claimed version once for the exception message — one extra
            // round trip only on the failure path, invaluable in production logs.
            var current = await versionsCollection.Find(x => x.Id!.Equals(id)).FirstOrDefaultAsync(cancellationToken);
            throw new ConcurrencyException<TId>(id, expectedVersion, current?.Version);
        }
    }

    private async Task TryRollbackVersionAsync<TAggregate, TId>(TId id, int fromVersion, int toVersion, CancellationToken cancellationToken)
    {
        try
        {
            var filter = Builders<AggregateVersionDao<TId>>.Filter.And(
                Builders<AggregateVersionDao<TId>>.Filter.Eq(x => x.Id, id),
                Builders<AggregateVersionDao<TId>>.Filter.Eq(x => x.Version, fromVersion));

            if (toVersion < 0)
                await GetVersionCollection<TAggregate, TId>().DeleteOneAsync(filter, cancellationToken);
            else
                await GetVersionCollection<TAggregate, TId>().UpdateOneAsync(filter,
                    Builders<AggregateVersionDao<TId>>.Update.Set(x => x.Version, toVersion), cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Version rollback for {AggregateType} {Id} failed; stream will self-heal on next load", typeof(TAggregate).Name, id);
        }
    }

    /// <summary>
    /// Self-heal: reconcile the version document with the events actually stored,
    /// in BOTH directions. Stored events are the truth — a write the server
    /// committed has happened even if the writer saw an error. Ahead (crash between
    /// version advance and event insert) is rolled back; behind (a rollback fired
    /// although the insert had committed server-side, e.g. during an election) is
    /// advanced; a missing document with existing events is recreated. Only runs
    /// for full loads (fromVersion &lt;= 0), because only they know the real last
    /// version. Guarded by a compare-and-set on the observed version so a concurrent
    /// writer is never overridden.
    /// </summary>
    private async Task HealVersionMismatchAsync<TAggregate, TId>(TId id, int fromVersion, List<EventEnvelope<TAggregate, TId>> loaded, CancellationToken cancellationToken)
        where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new()
    {
        if (fromVersion > 0)
            return;

        var versionsCollection = GetVersionCollection<TAggregate, TId>();
        var versionDoc = await versionsCollection.Find(x => x.Id!.Equals(id)).FirstOrDefaultAsync(cancellationToken);
        var storedLastVersion = loaded.Count == 0 ? -1 : loaded[^1].Metadata.Version;

        if (versionDoc == null)
        {
            if (storedLastVersion < 0)
                return;

            // Events exist but the version document is gone (rollback delete after a
            // committed insert of a new aggregate): recreate it so saves work again.
            _logger.LogWarning(
                "Healing {AggregateType} {Id}: version document missing but last stored event is {Stored}",
                typeof(TAggregate).Name, id, storedLastVersion);

            try
            {
                await versionsCollection.InsertOneAsync(
                    new AggregateVersionDao<TId> { Id = id, Version = storedLastVersion },
                    cancellationToken: cancellationToken);
            }
            catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                // A concurrent writer recreated it in the meantime — fine.
            }

            return;
        }

        if (versionDoc.Version == storedLastVersion)
            return;

        _logger.LogWarning(
            "Healing {AggregateType} {Id}: version document at {Claimed} but last stored event is {Stored}",
            typeof(TAggregate).Name, id, versionDoc.Version, storedLastVersion);

        var filter = Builders<AggregateVersionDao<TId>>.Filter.And(
            Builders<AggregateVersionDao<TId>>.Filter.Eq(x => x.Id, id),
            Builders<AggregateVersionDao<TId>>.Filter.Eq(x => x.Version, versionDoc.Version));

        if (storedLastVersion < 0)
            await versionsCollection.DeleteOneAsync(filter, cancellationToken);
        else
            await versionsCollection.UpdateOneAsync(filter,
                Builders<AggregateVersionDao<TId>>.Update.Set(x => x.Version, storedLastVersion), cancellationToken: cancellationToken);
    }

    private static TOut DeserializeFromBson<TOut>(BsonDocument bsonDocument) =>
        (TOut)BsonSerializer.Deserialize(bsonDocument, typeof(TOut));
}
