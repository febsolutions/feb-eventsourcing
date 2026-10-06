using FEB.EventSourcing.Snapshots;
using MongoDB.Bson.Serialization.Attributes;

namespace FEB.EventSourcing.MongoDb;

public sealed class SnapshotEnvelopeDao<TId>
{
    public SnapshotEnvelopeDao()
    {
        
    }
    
    public SnapshotEnvelopeDao(TId id, byte[] payload, int eventStreamVersion, int snapshotVersion, DateTime createdUtc, SnapshotCompression compression)
    {
        Id = id;
        Payload = payload;
        EventStreamVersion = eventStreamVersion;
        SnapshotVersion = snapshotVersion;
        CreatedUtc = createdUtc;
        Compression = compression;
    }

    [BsonId] 
    public TId Id { get; init; }
    public byte[] Payload { get; init; }
    public int EventStreamVersion { get; init; }
    public int SnapshotVersion { get; init; }
    public DateTime CreatedUtc { get; init; }
    public SnapshotCompression Compression { get; init; }
}