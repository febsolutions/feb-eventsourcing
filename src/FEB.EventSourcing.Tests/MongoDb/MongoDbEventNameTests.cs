using FEB.EventSourcing.Tests.Infrastructure;
using FEB.EventSourcing.Tests.TestDomain;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace FEB.EventSourcing.Tests.MongoDb;

/// <summary>
/// The BSON discriminator (<c>Payload._t</c>) is the stable name from [EventName];
/// older documents under the CLR short name and aliases stay readable.
/// </summary>
[Collection("mongo")]
public class MongoDbEventNameTests(MongoDbFixture fixture)
{
    private (ServiceProvider Provider, IMongoDatabase Db) BuildHost(string database)
    {
        var services = new ServiceCollection();
        var connectionString = fixture.GetConnectionString(database);

        services.AddEventSourcing(es =>
        {
            es.UseMongoDb(connectionString);
            es.Aggregate<Order, string>(a => a.Factory(Order.CreateNew));
        }, typeof(TestHost).Assembly);

        var sp = services.BuildServiceProvider();
        var db = new MongoClient(connectionString).GetDatabase(new MongoUrl(connectionString).DatabaseName);
        return (sp, db);
    }

    private static async Task<BsonDocument> SingleEventAsync(IMongoDatabase db, string id)
        => await db.GetCollection<BsonDocument>("EventStore.OrderEvents")
            .Find(Builders<BsonDocument>.Filter.Eq("Metadata.AggregateId", id)).SingleAsync();

    [Fact]
    public async Task Configured_name_becomes_the_bson_discriminator()
    {
        var (sp, db) = BuildHost("es_evname_write");
        await using var _ = sp;
        var store = sp.GetRequiredService<IEventStore<Order, string>>();
        var id = Guid.NewGuid().ToString("N");

        var order = Order.CreateNew(id);
        order.Rename("ACME");
        await store.SaveAsync(order, TestHost.NewCommandContext());

        var doc = await SingleEventAsync(db, id);
        doc["Payload"]["_t"].AsString.Should().Be("order.renamed",
            "the stable name is stored - only then may the event class move");

        var loaded = await store.LoadByIdAsync(id);
        loaded!.Customer.Should().Be("ACME");
    }

    [Fact]
    public async Task Events_without_the_attribute_keep_the_clr_discriminator()
    {
        var (sp, db) = BuildHost("es_evname_default");
        await using var _ = sp;
        var store = sp.GetRequiredService<IEventStore<Order, string>>();
        var id = Guid.NewGuid().ToString("N");

        var order = Order.CreateNew(id);
        order.Create("ACME");
        await store.SaveAsync(order, TestHost.NewCommandContext());

        (await SingleEventAsync(db, id))["Payload"]["_t"].AsString.Should().Be(nameof(OrderCreated),
            "existing applications without the attribute must keep working unchanged");
    }

    [Theory]
    [InlineData(nameof(OrderRenamed))]   // written before the attribute
    [InlineData("FEB.EventSourcing.Tests.TestDomain.Legacy.OrderRenamedV1, FEB.EventSourcing.Tests")]  // Alias
    public async Task Documents_with_an_older_discriminator_stay_readable(string oldDiscriminator)
    {
        var (sp, db) = BuildHost("es_evname_legacy");
        await using var _ = sp;
        var store = sp.GetRequiredService<IEventStore<Order, string>>();
        var id = Guid.NewGuid().ToString("N");

        var order = Order.CreateNew(id);
        order.Rename("ACME");
        await store.SaveAsync(order, TestHost.NewCommandContext());

        await db.GetCollection<BsonDocument>("EventStore.OrderEvents").UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("Metadata.AggregateId", id),
            Builders<BsonDocument>.Update.Set("Payload._t", oldDiscriminator));

        var loaded = await store.LoadByIdAsync(id);
        loaded!.Customer.Should().Be("ACME", "otherwise adopting the attribute would require a data migration");
    }
}
