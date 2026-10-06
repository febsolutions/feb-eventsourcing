using FEB.EventSourcing.Snapshots;
using FEB.EventSourcing.Tests.Infrastructure;
using FEB.EventSourcing.Tests.TestDomain;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace FEB.EventSourcing.Tests.MongoDb;

[Collection("mongo")]
public class MongoDbEventStoreTests(MongoDbFixture fixture)
{
    private ServiceProvider BuildHost(string database, bool backgroundSnapshots = false)
    {
        var services = new ServiceCollection();

        services.AddEventSourcing(es =>
        {
            es.UseMongoDb(fixture.GetConnectionString(database), o => o.EnableSnapshots());
            es.UseSnapshots();

            if (backgroundSnapshots)
                es.UseBackgroundSnapshotWrites();

            es.Aggregate<Order, string>(a => a.Factory(Order.CreateNew));
            es.Aggregate<Customer, string>(a =>
            {
                a.Factory(Customer.CreateNew);
                a.Snapshots(s => s.EveryNEvents(2));
            });
        }, typeof(TestHost).Assembly);

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Save_and_load_roundtrip_restores_state_and_version()
    {
        await using var sp = BuildHost("es_roundtrip");
        var store = sp.GetRequiredService<IEventStore<Order, string>>();
        var id = NewId();

        var order = Order.CreateNew(id);
        order.Create("ACME");
        order.AddItem("Widget", 9.99m);
        await store.SaveAsync(order, TestHost.NewCommandContext());

        var loaded = await store.LoadByIdAsync(id);

        loaded.Should().NotBeNull();
        loaded!.Version.Should().Be(1);
        loaded.Customer.Should().Be("ACME");
        loaded.Items.Should().ContainSingle().Which.Should().Be("Widget");
        loaded.Total.Should().Be(9.99m);
    }

    [Fact]
    public async Task Event_metadata_roundtrips_through_bson()
    {
        await using var sp = BuildHost("es_metadata");
        var store = sp.GetRequiredService<IEventStore<Order, string>>();
        var id = NewId();
        var context = TestHost.NewCommandContext();

        var order = Order.CreateNew(id);
        order.Create("ACME");
        await store.SaveAsync(order, context);

        var envelopes = await store.LoadEventsAsync(id);

        var envelope = envelopes.Should().ContainSingle().Subject;
        envelope.Payload.Should().BeOfType<OrderCreated>()
            .Which.Customer.Should().Be("ACME");
        envelope.Metadata.AggregateId.Should().Be(id);
        envelope.Metadata.Version.Should().Be(0);
        envelope.Metadata.CorrelationId.Should().Be(context.CorrelationId);
        envelope.Metadata.CausationId.Should().Be(context.CommandId.ToString());
    }

    [Fact]
    public async Task IsExists_and_GetAllIds_reflect_saved_aggregates()
    {
        await using var sp = BuildHost("es_exists");
        var store = sp.GetRequiredService<IEventStore<Order, string>>();
        var id1 = NewId();
        var id2 = NewId();

        foreach (var id in new[] { id1, id2 })
        {
            var order = Order.CreateNew(id);
            order.Create("Customer");
            await store.SaveAsync(order, TestHost.NewCommandContext());
        }

        (await store.IsExistsAsync(id1)).Should().BeTrue();
        (await store.IsExistsAsync(NewId())).Should().BeFalse();
        (await store.GetAllIdsAsync()).Should().Contain([id1, id2]);
    }

    [Fact]
    public async Task Concurrent_save_with_stale_version_throws_concurrency_exception()
    {
        await using var sp = BuildHost("es_concurrency");
        var store = sp.GetRequiredService<IEventStore<Order, string>>();
        var id = NewId();

        var order = Order.CreateNew(id);
        order.Create("ACME");
        await store.SaveAsync(order, TestHost.NewCommandContext());

        var copy1 = await store.LoadByIdAsync(id);
        var copy2 = await store.LoadByIdAsync(id);

        copy1!.AddItem("Widget", 1m);
        await store.SaveAsync(copy1, TestHost.NewCommandContext());

        copy2!.AddItem("Gadget", 2m);
        var act = () => store.SaveAsync(copy2, TestHost.NewCommandContext());

        await act.Should().ThrowAsync<ConcurrencyException<string>>();
    }

    [Fact]
    public async Task Aggregate_with_snapshot_loads_correctly_after_snapshot_was_taken()
    {
        await using var sp = BuildHost("es_snapshots");
        var store = sp.GetRequiredService<IEventStore<Customer, string>>();
        var id = NewId();

        // 3 events => version 2, snapshot cadence EveryNEvents(2) applies at version 2
        var customer = Customer.CreateNew(id);
        customer.Register("ACME GmbH", CustomerKind.Company, DateTime.UtcNow);
        customer.Relocate("Hauptstraße 1", "Berlin");
        customer.AddContact("Alex", "alex@acme.test");
        await store.SaveAsync(customer, TestHost.NewCommandContext());

        var loaded = await store.LoadByIdAsync(id);

        loaded.Should().NotBeNull();
        loaded!.Version.Should().Be(2, "snapshot + delta must not apply any event twice");
        loaded.Name.Should().Be("ACME GmbH");
        loaded.Address!.City.Should().Be("Berlin");
        loaded.Contacts.Should().ContainSingle("the contact event must not be applied twice");
    }

    [Fact]
    public async Task Saving_after_snapshot_load_does_not_conflict()
    {
        await using var sp = BuildHost("es_snapshots_save");
        var store = sp.GetRequiredService<IEventStore<Customer, string>>();
        var id = NewId();

        var customer = Customer.CreateNew(id);
        customer.Register("ACME GmbH", CustomerKind.Company, DateTime.UtcNow);
        customer.Relocate("Hauptstraße 1", "Berlin");
        await store.SaveAsync(customer, TestHost.NewCommandContext());

        var loaded = await store.LoadByIdAsync(id);
        loaded!.AddContact("Alex", "alex@acme.test");
        await store.SaveAsync(loaded, TestHost.NewCommandContext());

        var reloaded = await store.LoadByIdAsync(id);
        reloaded!.Version.Should().Be(2);
        reloaded.Contacts.Should().ContainSingle();
    }

    [Fact]
    public async Task Snapshot_with_delta_events_replays_only_the_delta()
    {
        await using var sp = BuildHost("es_snapshots_delta");
        var store = sp.GetRequiredService<IEventStore<Customer, string>>();
        var id = NewId();

        // Crossing cadence, N=2 (by event count = version + 1):
        // save 1: 2 events => version 1 (2 events -> multiple of 2 crossed -> snapshot v1)
        var customer = Customer.CreateNew(id);
        customer.Register("ACME GmbH", CustomerKind.Company, DateTime.UtcNow);
        customer.Relocate("Hauptstraße 1", "Berlin");
        await store.SaveAsync(customer, TestHost.NewCommandContext());

        // save 2: 1 event => version 2 (3 events -> no new multiple -> no snapshot)
        customer.AddContact("Alex", "alex@acme.test");
        await store.SaveAsync(customer, TestHost.NewCommandContext());

        // Save 3: 1 Event => Version 3 (4 Events → Snapshot v3) => Load = Snapshot + ggf. Delta
        customer.AddContact("Kim", "kim@acme.test");
        await store.SaveAsync(customer, TestHost.NewCommandContext());

        var loaded = await store.LoadByIdAsync(id);

        loaded!.Version.Should().Be(3);
        loaded.Contacts.Should().HaveCount(2);
        loaded.Contacts.Select(c => c.Name).Should().BeEquivalentTo("Alex", "Kim");
    }

    [Fact]
    public async Task Background_snapshot_write_persists_snapshot_without_blocking_save()
    {
        await using var sp = BuildHost("es_snapshots_bg", backgroundSnapshots: true);

        // Hosted services do not start by themselves in a bare ServiceProvider
        var workers = sp.GetServices<Microsoft.Extensions.Hosting.IHostedService>().ToList();
        foreach (var worker in workers)
            await worker.StartAsync(CancellationToken.None);

        try
        {
            var store = sp.GetRequiredService<IEventStore<Customer, string>>();
            var id = NewId();

            var customer = Customer.CreateNew(id);
            customer.Register("ACME GmbH", CustomerKind.Company, DateTime.UtcNow);
            customer.Relocate("Hauptstraße 1", "Berlin");
            customer.AddContact("Alex", "alex@acme.test");
            await store.SaveAsync(customer, TestHost.NewCommandContext());

            // the snapshot is written asynchronously => poll until it exists
            var persistence = sp.GetRequiredService<FEB.EventSourcing.Snapshots.ISnapshotPersistence>();
            var registry = sp.GetRequiredService<FEB.EventSourcing.Snapshots.ISnapshotMetadataRegistry>();
            var meta = registry.GetForAggregate(typeof(Customer))!;

            object? snapshot = null;
            for (var i = 0; i < 50 && snapshot == null; i++)
            {
                var result = await persistence.LoadAsync<Customer, string>(id, meta.SnapshotType, meta.Version);
                snapshot = result?.Snapshot;
                if (snapshot == null)
                    await Task.Delay(100);
            }

            snapshot.Should().NotBeNull("the background worker must write the snapshot");

            var loaded = await store.LoadByIdAsync(id);
            loaded!.Version.Should().Be(2);
            loaded.Contacts.Should().ContainSingle();
        }
        finally
        {
            foreach (var worker in workers)
                await worker.StopAsync(CancellationToken.None);
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N");
}
