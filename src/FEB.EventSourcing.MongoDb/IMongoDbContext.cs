using MongoDB.Bson;
using MongoDB.Driver;

namespace FEB.EventSourcing.MongoDb;

public interface IMongoDbContext
{
    IMongoCollection<TMongoEntity> GetCollection<TMongoEntity>(string name);
    IMongoCollection<BsonDocument> GetCollection(string name);
    
    IMongoClient Client { get; }
    IMongoDatabase Database { get; }
}