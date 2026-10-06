using MongoDB.Bson.Serialization.Attributes;

namespace FEB.EventSourcing.MongoDb;

public class AggregateVersionDao<TId>
{
    [BsonId]
    public TId Id { get; set; }
    public int Version { get; set; }
}