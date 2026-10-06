using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace FEB.EventSourcing.MongoDb;

public class EventEnvelopeDao<TId>
{
    public EventEnvelopeDao()
    {
        
    }
    
    public EventEnvelopeDao(BsonDocument payload, EventMetadataDao<TId> metadata)
    {
        Id = metadata.EventId.ToString();
        Metadata = metadata;
        Payload = payload;
    }

    [BsonId]
    public string Id { get; set; }

    [BsonElement("Metadata")] public EventMetadataDao<TId> Metadata { get; set; } = null!;
    
    [BsonElement("Payload")] public BsonDocument Payload { get; set; } = null!; 
}