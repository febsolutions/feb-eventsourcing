using FEB.EventSourcing.MongoDb;
using FEB.EventSourcing.Tests.Infrastructure;
using FEB.EventSourcing.Tests.TestDomain;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace FEB.EventSourcing.Tests.MongoDb;

/// <summary>Atomicity without a replica set: unique index, self-heal, index initialization.</summary>
[Collection("mongo")]
public class MongoDbConsistencyTests(MongoDbFixture fixture)
{
    private (ServiceProvider Provider, IMongoDatabase Db) BuildHost(string database, Action<MongoOutboxOptions>? outboxOptions = null)
    {
        var services = new ServiceCollection();
        var connectionString = fixture.GetConnectionString(database);

        services.AddEventSourcing(es =>
        {
            es.UseMongoDb(connectionString);
            es.UseMongoOutbox(outboxOptions ?? (_ => { }));
            es.Aggregate<Order, string>(a => a.Factory(Order.CreateNew));
        }, typeof(TestHost).Assembly);

        var sp = services.BuildServiceProvider();
        var db = new MongoClient(connectionString).GetDatabase(new MongoUrl(connectionString).DatabaseName);
        return (sp, db);
    }

    private static async Task RunIndexInitializerAsync(ServiceProvider sp)
    {
        var initializer = sp.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<MongoDbIndexInitializer>().Single();
        await initializer.EnsureAllAsync();
    }

    [Fact]
    public async Task Startup_creates_unique_event_index_and_outbox_index()
    {
        var (sp, db) = BuildHost("es_indexes");
        await using var _ = sp;

        await RunIndexInitializerAsync(sp);

        var eventIndexes = await (await db.GetCollection<MongoDB.Bson.BsonDocument>("EventStore.OrderEvents").Indexes.ListAsync()).ToListAsync();
        eventIndexes.Should().Contain(i => i["name"] == "ux_aggregate_version" && i["unique"] == true);

        var outboxIndexes = await (await db.GetCollection<MongoDB.Bson.BsonDocument>("outbox").Indexes.ListAsync()).ToListAsync();
        var dequeue = outboxIndexes.Should().Contain(i => i["name"] == "ix_dequeue").Subject;
        dequeue["key"].AsBsonDocument.Names.Should().Equal(new[] { "DispatchedAt", "CreatedAt" },
            "the dequeue query (equality on DispatchedAt=null + sort by CreatedAt) must be able to sort using the index; LockedUntil inside the index breaks that");
    }

    [Fact]
    public async Task Old_dequeue_index_spec_is_migrated_automatically()
    {
        var (sp, db) = BuildHost("es_index_migration");
        await using var _ = sp;

        // Create the old index spec (pre-9.0.0-beta.2): same name, different keys
        var outbox = db.GetCollection<MongoDB.Bson.BsonDocument>("outbox");
        await outbox.Indexes.CreateOneAsync(new CreateIndexModel<MongoDB.Bson.BsonDocument>(
            Builders<MongoDB.Bson.BsonDocument>.IndexKeys
                .Ascending("DispatchedAt").Ascending("LockedUntil").Ascending("CreatedAt"),
            new CreateIndexOptions { Name = "ix_dequeue" }));

        await RunIndexInitializerAsync(sp);

        var indexes = await (await outbox.Indexes.ListAsync()).ToListAsync();
        var dequeue = indexes.Single(i => i["name"] == "ix_dequeue");
        dequeue["key"].AsBsonDocument.Names.Should().Equal(new[] { "DispatchedAt", "CreatedAt" },
            "the old three-column index must be replaced by the new one at startup");
    }

    [Fact]
    public async Task Ttl_index_enforces_default_retention_of_seven_days()
    {
        var (sp, db) = BuildHost("es_ttl_default");
        await using var _ = sp;

        await RunIndexInitializerAsync(sp);

        var indexes = await (await db.GetCollection<MongoDB.Bson.BsonDocument>("outbox").Indexes.ListAsync()).ToListAsync();
        var ttl = indexes.Should().Contain(i => i["name"] == "ttl_dispatched").Subject;
        ttl["expireAfterSeconds"].ToInt64().Should().Be((long)TimeSpan.FromDays(7).TotalSeconds);
    }

    [Fact]
    public async Task Changed_retention_migrates_the_ttl_index()
    {
        var (sp1, _) = BuildHost("es_ttl_change");
        await using (sp1)
            await RunIndexInitializerAsync(sp1);

        var (sp2, db) = BuildHost("es_ttl_change", o => o.SetCompletedRetention(TimeSpan.FromHours(1)));
        await using var __ = sp2;
        await RunIndexInitializerAsync(sp2);

        var indexes = await (await db.GetCollection<MongoDB.Bson.BsonDocument>("outbox").Indexes.ListAsync()).ToListAsync();
        indexes.Single(i => i["name"] == "ttl_dispatched")["expireAfterSeconds"].ToInt64()
            .Should().Be(3600, "a changed retention must replace the TTL index");
    }

    [Fact]
    public async Task Disabled_cleanup_drops_the_ttl_index()
    {
        var (sp1, _) = BuildHost("es_ttl_disable");
        await using (sp1)
            await RunIndexInitializerAsync(sp1);

        var (sp2, db) = BuildHost("es_ttl_disable", o => o.DisableCompletedCleanup());
        await using var __ = sp2;
        await RunIndexInitializerAsync(sp2);

        var indexes = await (await db.GetCollection<MongoDB.Bson.BsonDocument>("outbox").Indexes.ListAsync()).ToListAsync();
        indexes.Should().NotContain(i => i["name"] == "ttl_dispatched");
    }

    [Fact]
    public async Task Index_initialization_is_idempotent()
    {
        var (sp, _) = BuildHost("es_indexes_idem");
        await using var __ = sp;

        await RunIndexInitializerAsync(sp);
        var act = () => RunIndexInitializerAsync(sp);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Version_ahead_of_events_is_healed_on_load_and_save_succeeds_again()
    {
        var (sp, db) = BuildHost("es_selfheal");
        await using var _ = sp;
        var store = sp.GetRequiredService<IEventStore<Order, string>>();
        var id = Guid.NewGuid().ToString("N");

        var order = Order.CreateNew(id);
        order.Create("ACME");
        await store.SaveAsync(order, TestHost.NewCommandContext());

        // Simulate a crash between version advance and event insert:
        // the version claims 5, only event v0 is stored.
        var versions = db.GetCollection<AggregateVersionDao<string>>("EventStore.OrderVersions");
        await versions.UpdateOneAsync(v => v.Id == id, Builders<AggregateVersionDao<string>>.Update.Set(v => v.Version, 5));

        var loaded = await store.LoadByIdAsync(id);
        loaded!.Version.Should().Be(0, "what is loaded is what is really stored");

        // Without self-heal this save would fail on version 5
        loaded.AddItem("Widget", 1m);
        await store.SaveAsync(loaded, TestHost.NewCommandContext());

        var reloaded = await store.LoadByIdAsync(id);
        reloaded!.Version.Should().Be(1);
        reloaded.Items.Should().ContainSingle();
    }

    [Fact]
    public async Task Version_behind_events_is_healed_on_load_and_save_succeeds_again()
    {
        // InsertMany "fails" on the client side (election/timeout on a single-node replica
        // set) but was committed on the server; the rollback then moves the version
        // document BEHIND the last stored event. Without healing in this direction the
        // stream would be blocked permanently.
        var (sp, db) = BuildHost("es_selfheal_behind");
        await using var _ = sp;
        var store = sp.GetRequiredService<IEventStore<Order, string>>();
        var id = Guid.NewGuid().ToString("N");

        var order = Order.CreateNew(id);
        order.Create("ACME");
        await store.SaveAsync(order, TestHost.NewCommandContext());
        order.AddItem("Widget", 1m);
        await store.SaveAsync(order, TestHost.NewCommandContext());   // events v0..v1, version document 1

        // Simulate the faulty rollback: document claims 0, events are stored up to v1
        var versions = db.GetCollection<AggregateVersionDao<string>>("EventStore.OrderVersions");
        await versions.UpdateOneAsync(v => v.Id == id, Builders<AggregateVersionDao<string>>.Update.Set(v => v.Version, 0));

        var loaded = await store.LoadByIdAsync(id);
        loaded!.Version.Should().Be(1, "what is loaded is what is really stored");

        // Without healing in this direction every further save would fail forever
        loaded.AddItem("Gadget", 2m);
        await store.SaveAsync(loaded, TestHost.NewCommandContext());

        var reloaded = await store.LoadByIdAsync(id);
        reloaded!.Version.Should().Be(2);
        reloaded.Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task Orphaned_event_beyond_version_doc_must_not_swallow_new_events()
    {
        // If an orphaned event n+1 exists (insert committed, rollback committed),
        // the next append hits the duplicate key. That must never be treated as an
        // idempotent retry - otherwise the new event would be lost SILENTLY.
        var (sp, db) = BuildHost("es_orphan_event");
        await using var _ = sp;
        await RunIndexInitializerAsync(sp);
        var store = sp.GetRequiredService<IEventStore<Order, string>>();
        var id = Guid.NewGuid().ToString("N");

        var order = Order.CreateNew(id);
        order.Create("ACME");
        await store.SaveAsync(order, TestHost.NewCommandContext());   // event v0, document 0

        // Create an orphaned event v1 (copy of v0 with version 1 and its own EventId)
        var events = db.GetCollection<MongoDB.Bson.BsonDocument>("EventStore.OrderEvents");
        var orphan = await events.Find(FilterDefinition<MongoDB.Bson.BsonDocument>.Empty).SingleAsync();
        var orphanEventId = Guid.NewGuid();
        orphan["_id"] = orphanEventId.ToString();
        orphan["Metadata"]["Version"] = 1;
        orphan["Metadata"]["EventId"] = new MongoDB.Bson.BsonBinaryData(orphanEventId, MongoDB.Bson.GuidRepresentation.Standard);
        await events.InsertOneAsync(orphan);

        var loaded = await store.LoadByIdAsync(id);
        loaded!.AddItem("Widget", 1m);

        // The save must NOT silently "succeed" and discard the new event:
        // either the conflict is reported or the event really ends up in the stream.
        try
        {
            await store.SaveAsync(loaded, TestHost.NewCommandContext());
            var reloaded = await store.LoadByIdAsync(id);
            reloaded!.Items.Should().ContainSingle("a confirmed save must really have stored the new event");
        }
        catch (ConcurrencyException<string>)
        {
            // Reporting the conflict is correct as well - what matters is no silent loss.
        }
    }

    [Fact]
    public async Task Two_step_retry_with_own_events_completes_the_missing_remainder()
    {
        // Idempotent retry after a crash between the steps: of two events only the
        // first was inserted, and the rollback went through. The retry with the same
        // EventIds must insert the missing remainder - not blindly report "success".
        var (sp, db) = BuildHost("es_retry_completion");
        await using var _ = sp;
        await RunIndexInitializerAsync(sp);
        var persistence = sp.GetRequiredService<IEventStorePersistence>();
        var id = Guid.NewGuid().ToString("N");

        await persistence.AppendEventsAsync(id, -1, [TestHost.NewEnvelope(id, 0, new OrderCreated("ACME"))]);

        var e1 = TestHost.NewEnvelope(id, 1, new OrderItemAdded("Widget", 1m));
        var e2 = TestHost.NewEnvelope(id, 2, new OrderItemAdded("Gadget", 2m));
        await persistence.AppendEventsAsync(id, 0, [e1, e2]);

        // Simulate the crash: v2 is missing, the rollback reset the document to 0
        var events = db.GetCollection<MongoDB.Bson.BsonDocument>("EventStore.OrderEvents");
        await events.DeleteOneAsync(d => d["Metadata"]["Version"] == 2);
        var versions = db.GetCollection<AggregateVersionDao<string>>("EventStore.OrderVersions");
        await versions.UpdateOneAsync(v => v.Id == id, Builders<AggregateVersionDao<string>>.Update.Set(v => v.Version, 0));

        // Retry with the same envelopes (same EventIds)
        await persistence.AppendEventsAsync(id, 0, [e1, e2]);

        var stored = await persistence.LoadEventsAsync<Order, string>(id);
        stored.Should().HaveCount(3);
        stored.Select(x => x.Metadata.EventId).Should().Contain([e1.Metadata.EventId, e2.Metadata.EventId]);
        (await versions.Find(v => v.Id == id).SingleAsync()).Version.Should().Be(2);
    }

    [Fact]
    public async Task Two_step_append_against_foreign_orphan_throws_with_actual_version()
    {
        var (sp, db) = BuildHost("es_orphan_direct");
        await using var _ = sp;
        await RunIndexInitializerAsync(sp);
        var persistence = sp.GetRequiredService<IEventStorePersistence>();
        var id = Guid.NewGuid().ToString("N");

        await persistence.AppendEventsAsync(id, -1, [TestHost.NewEnvelope(id, 0, new OrderCreated("ACME"))]);

        // Foreign orphan at v1 (left over by an earlier append, different EventId)
        await persistence.AppendEventsAsync(id, 0, [TestHost.NewEnvelope(id, 1, new OrderItemAdded("Orphan", 9m))]);
        var versions = db.GetCollection<AggregateVersionDao<string>>("EventStore.OrderVersions");
        await versions.UpdateOneAsync(v => v.Id == id, Builders<AggregateVersionDao<string>>.Update.Set(v => v.Version, 0));

        var act = () => persistence.AppendEventsAsync(id, 0, [TestHost.NewEnvelope(id, 1, new OrderItemAdded("Neu", 1m))]);

        var ex = (await act.Should().ThrowAsync<ConcurrencyException<string>>()).Which;
        ex.ActualVersion.Should().Be(1, "the exception should name the actually stored state");

        (await persistence.LoadEventsAsync<Order, string>(id)).Should().HaveCount(2, "the new event must not be inserted and the orphan must not be replaced");
    }

    [Fact]
    public async Task Missing_version_document_with_events_is_recreated_on_load()
    {
        var (sp, db) = BuildHost("es_heal_missing_doc");
        await using var _ = sp;
        var store = sp.GetRequiredService<IEventStore<Order, string>>();
        var id = Guid.NewGuid().ToString("N");

        var order = Order.CreateNew(id);
        order.Create("ACME");
        await store.SaveAsync(order, TestHost.NewCommandContext());

        // Simulate a rollback delete after the committed insert of a new aggregate
        var versions = db.GetCollection<AggregateVersionDao<string>>("EventStore.OrderVersions");
        await versions.DeleteOneAsync(v => v.Id == id);

        var loaded = await store.LoadByIdAsync(id);
        loaded!.AddItem("Widget", 1m);
        await store.SaveAsync(loaded, TestHost.NewCommandContext());

        (await store.LoadByIdAsync(id))!.Version.Should().Be(1);
    }

    [Fact]
    public async Task Concurrent_creation_of_the_same_new_aggregate_yields_exactly_one_winner()
    {
        var (sp, _) = BuildHost("es_create_race");
        await using var __ = sp;
        var store = sp.GetRequiredService<IEventStore<Order, string>>();
        var id = Guid.NewGuid().ToString("N");

        var attempts = Enumerable.Range(0, 8).Select(async i =>
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
        });

        var results = await Task.WhenAll(attempts);

        results.Count(r => r).Should().Be(1, "exactly one writer may create a new aggregate");
        (await store.LoadEventsAsync(id)).Should().ContainSingle();
    }
}
