using FEB.EventSourcing.MongoDb;
using FEB.EventSourcing.Tests.Infrastructure;
using FEB.EventSourcing.Tests.TestDomain;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace FEB.EventSourcing.Tests.MongoDb;

/// <summary>
/// Transactional mode (`UseTransactions`): version compare-and-set and
/// event insert are atomic - no intermediate states, nothing to heal.
/// Runs against the fixture's single-node replica set.
/// </summary>
[Collection("mongo")]
public class MongoDbTransactionTests(MongoDbFixture fixture)
{
    private (ServiceProvider Provider, IMongoDatabase Db) BuildHost(string database)
    {
        var services = new ServiceCollection();
        var connectionString = fixture.GetConnectionString(database);

        services.AddEventSourcing(es =>
        {
            es.UseMongoDb(connectionString, o => o.UseTransactions());
            es.Aggregate<Order, string>(a => a.Factory(Order.CreateNew));
        }, typeof(TestHost).Assembly);

        var sp = services.BuildServiceProvider();
        var db = new MongoClient(connectionString).GetDatabase(new MongoUrl(connectionString).DatabaseName);
        return (sp, db);
    }

    [Fact]
    public async Task Transactional_roundtrip_saves_and_loads()
    {
        var (sp, _) = BuildHost("es_tx_roundtrip");
        await using var _1 = sp;
        var store = sp.GetRequiredService<IEventStore<Order, string>>();
        var id = Guid.NewGuid().ToString("N");

        var order = Order.CreateNew(id);
        order.Create("ACME");
        order.AddItem("Widget", 9.99m);
        await store.SaveAsync(order, TestHost.NewCommandContext());
        order.AddItem("Gadget", 2m);
        await store.SaveAsync(order, TestHost.NewCommandContext());

        var loaded = await store.LoadByIdAsync(id);
        loaded!.Version.Should().Be(2);
        loaded.Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task Transactional_stale_writer_gets_concurrency_exception_with_actual_version()
    {
        var (sp, _) = BuildHost("es_tx_stale");
        await using var _1 = sp;
        var store = sp.GetRequiredService<IEventStore<Order, string>>();
        var id = Guid.NewGuid().ToString("N");

        var order = Order.CreateNew(id);
        order.Create("ACME");
        await store.SaveAsync(order, TestHost.NewCommandContext());

        var copy1 = await store.LoadByIdAsync(id);
        var copy2 = await store.LoadByIdAsync(id);
        copy1!.AddItem("Widget", 1m);
        await store.SaveAsync(copy1, TestHost.NewCommandContext());

        copy2!.AddItem("Gadget", 2m);
        var act = () => store.SaveAsync(copy2, TestHost.NewCommandContext());

        var ex = (await act.Should().ThrowAsync<ConcurrencyException<string>>()).Which;
        ex.ExpectedVersion.Should().Be(0);
        ex.ActualVersion.Should().Be(1, "the exception should carry the actual state for diagnosis");

        (await store.LoadEventsAsync(id)).Should().HaveCount(2, "the losing writer must not leave anything behind");
    }

    [Fact]
    public async Task Transactional_append_against_orphan_event_aborts_atomically()
    {
        // Proof of atomicity: if the insert hits an (older) orphan, the
        // transaction aborts - including the version advance that already happened.
        // Directly on the persistence, so that no load-time healing interferes.
        var (sp, db) = BuildHost("es_tx_orphan");
        await using var _1 = sp;
        await sp.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<MongoDbIndexInitializer>().Single().EnsureAllAsync();
        var store = sp.GetRequiredService<IEventStore<Order, string>>();
        var persistence = sp.GetRequiredService<IEventStorePersistence>();
        var id = Guid.NewGuid().ToString("N");

        var order = Order.CreateNew(id);
        order.Create("ACME");
        await store.SaveAsync(order, TestHost.NewCommandContext());   // Event v0, Doc 0

        var events = db.GetCollection<MongoDB.Bson.BsonDocument>("EventStore.OrderEvents");
        var orphan = await events.Find(FilterDefinition<MongoDB.Bson.BsonDocument>.Empty).SingleAsync();
        var orphanEventId = Guid.NewGuid();
        orphan["_id"] = orphanEventId.ToString();
        orphan["Metadata"]["Version"] = 1;
        orphan["Metadata"]["EventId"] = new MongoDB.Bson.BsonBinaryData(orphanEventId, MongoDB.Bson.GuidRepresentation.Standard);
        await events.InsertOneAsync(orphan);

        var act = () => persistence.AppendEventsAsync(id, 0,
            [TestHost.NewEnvelope(id, version: 1, new OrderItemAdded("Widget", 1m))]);

        await act.Should().ThrowAsync<ConcurrencyException<string>>();

        var versions = db.GetCollection<AggregateVersionDao<string>>("EventStore.OrderVersions");
        var doc = await versions.Find(v => v.Id == id).SingleAsync();
        doc.Version.Should().Be(0, "the aborted transaction must also undo the version advance");
        (await events.CountDocumentsAsync(FilterDefinition<MongoDB.Bson.BsonDocument>.Empty)).Should().Be(2);
    }

    [Fact]
    public async Task Transactional_failed_outbox_write_rolls_back_the_events()
    {
        // With UseTransactions the outbox envelopes are part of the append transaction.
        var services = new ServiceCollection();
        var connectionString = fixture.GetConnectionString("es_tx_outbox_atomic");
        services.AddEventSourcing(es =>
        {
            es.UseMongoDb(connectionString, o => o.UseTransactions());
            es.UseMongoOutbox();
            es.Aggregate<Order, string>(a => a.Factory(Order.CreateNew));
        }, typeof(TestHost).Assembly);
        await using var sp = services.BuildServiceProvider();
        await sp.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<MongoDbIndexInitializer>().Single().EnsureAllAsync();

        // Make every outbox write fail: a validator that rejects all documents.
        var db = new MongoClient(connectionString).GetDatabase(new MongoUrl(connectionString).DatabaseName);
        await db.RunCommandAsync<MongoDB.Bson.BsonDocument>(new MongoDB.Bson.BsonDocument
        {
            { "collMod", "outbox" },
            { "validator", new MongoDB.Bson.BsonDocument("$jsonSchema",
                new MongoDB.Bson.BsonDocument("required", new MongoDB.Bson.BsonArray { "never-present" })) },
            { "validationAction", "error" }
        });

        var store = sp.GetRequiredService<IEventStore<Order, string>>();
        var id = Guid.NewGuid().ToString("N");
        var order = Order.CreateNew(id);
        order.Create("ACME");

        var save = () => store.SaveAsync(order, TestHost.NewCommandContext());
        await save.Should().ThrowAsync<Exception>("the outbox write failed");

        (await store.LoadEventsAsync(id)).Should().BeEmpty("events and outbox envelopes are one transaction");
        (await store.IsExistsAsync(id)).Should().BeFalse("the version advance is rolled back as well");
    }

    [Fact]
    public async Task Transactional_concurrent_creation_yields_exactly_one_winner()
    {
        var (sp, _) = BuildHost("es_tx_create_race");
        await using var _1 = sp;
        var store = sp.GetRequiredService<IEventStore<Order, string>>();
        var id = Guid.NewGuid().ToString("N");

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            var order = Order.CreateNew(id);
            order.Create($"writer-{i}");
            try
            {
                await store.SaveAsync(order, TestHost.NewCommandContext());
                return true;
            }
            catch (ConcurrencyException<string>)
            {
                return false;
            }
        }));

        results.Count(r => r).Should().Be(1);
        (await store.LoadEventsAsync(id)).Should().ContainSingle();
    }
}
