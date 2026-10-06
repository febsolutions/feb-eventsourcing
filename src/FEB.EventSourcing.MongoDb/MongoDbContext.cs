using MongoDB.Bson;
using MongoDB.Driver;

namespace FEB.EventSourcing.MongoDb;

public class MongoDbContext : IMongoDbContext
{
    public IMongoClient Client { get; }
    public IMongoDatabase Database { get; }

    public MongoDbContext(string connectionString)
    {
        var url = new MongoUrl(connectionString);
        Client = new MongoClient(url);
        Database = Client.GetDatabase(url.DatabaseName);
    }

    public IMongoCollection<TMongoEntity> GetCollection<TMongoEntity>(string name) =>
        Database.GetCollection<TMongoEntity>(name);

    public IMongoCollection<BsonDocument> GetCollection(string name) => Database.GetCollection<BsonDocument>(name);
}