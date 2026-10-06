using System.Diagnostics;
using FEB.EventSourcing.Snapshots;
using MongoDB.Driver;
using MongoDB.Driver.Linq;

namespace FEB.EventSourcing.MongoDb;

public class MongoDbSnapshotPersistence(
    IMongoDbContext dbContext,
    EventStoreCollectionNameBuilder collectionNameBuilder,
    MongoDbSnapshotSerializer? snapshotSerializer,
    MongoEventStoreOptions options,
    IEventStoreMetrics? mayBeMetrics)
    : ISnapshotPersistence
{
    // Snapshotting
    const string SnapshotMetricSource = "mongodb-snapshots";
   
    private readonly SnapshotCompression _compression = options.Compression;

    private readonly IEventStoreMetrics _metrics = mayBeMetrics ?? new NoOpEventStoreMetrics();
    
    public async Task SaveAsync<TAggregate, TId>(object value, Type snapshotType, TId id, int aggregateVersion, int schemaVersion, CancellationToken cancellationToken = default)
    {
        if (snapshotSerializer == null)
            return;
        
        var sw = Stopwatch.StartNew();

        var snapshotCollection = GetSnapshotCollection<TAggregate, TId>();

        var data = snapshotSerializer.Serialize(value, snapshotType, _compression);
        
        var envelope = new SnapshotEnvelopeDao<TId>(id, data, aggregateVersion, schemaVersion, DateTime.UtcNow, _compression);
        var filter = Builders<SnapshotEnvelopeDao<TId>>.Filter.Eq(s => s.Id, id);

        await snapshotCollection.ReplaceOneAsync(filter, envelope, new ReplaceOptions { IsUpsert = true }, cancellationToken);

        
        sw.Stop();
        _metrics.IncrementWrite(snapshotType, SnapshotMetricSource);
        _metrics.RecordWrite(snapshotType, SnapshotMetricSource, sw.Elapsed.TotalSeconds);

    }
    
    public async Task<StoredSnapshot?> LoadAsync<TAggregate, TId>(TId id, Type type, int expectedSchemaVersion, CancellationToken cancellationToken = default)
    {
        if (snapshotSerializer == null)
            return null;
        
        var sw = Stopwatch.StartNew();
        
        var snapshotCollection = GetSnapshotCollection<TAggregate, TId>();
        
        var envelope = await snapshotCollection
            .AsQueryable()
            .SingleOrDefaultAsync(dao => dao.Id!.Equals(id), cancellationToken);

        sw.Stop();
        _metrics.IncrementRead(type, SnapshotMetricSource);
        _metrics.RecordRead(type, SnapshotMetricSource, sw.Elapsed.TotalSeconds);

        if (envelope == null || envelope.SnapshotVersion != expectedSchemaVersion)
            return null;

        var snapshot = snapshotSerializer.Deserialize(envelope.Payload, type, envelope.Compression);

        return new StoredSnapshot(snapshot, envelope.EventStreamVersion);
    }
    
    private IMongoCollection<SnapshotEnvelopeDao<TId>> GetSnapshotCollection<TAggregate, TId>() => dbContext.GetCollection<SnapshotEnvelopeDao<TId>>(collectionNameBuilder.GetSnapshotName<TAggregate>());

}