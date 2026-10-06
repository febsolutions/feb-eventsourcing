using FEB.EventSourcing.Tests.Infrastructure;
using FEB.EventSourcing.Tests.TestDomain;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace FEB.EventSourcing.Tests.Persistence;

/// <summary>
/// The persistence contract every provider must fulfil identically. Concrete test
/// classes (MongoDB, Postgres, SQL Server) only supply the host wiring; every test
/// here runs once per provider.
/// </summary>
public abstract class PersistenceContractTests
{
    /// <summary>Builds a host with persistence + snapshots (Customer: EveryNEvents(2)) + outbox + worker.</summary>
    protected abstract ServiceProvider BuildHost(string suffix, bool withOutbox = false);

    /// <summary>Runs whatever startup initialization the provider needs (indexes/schema).</summary>
    protected abstract Task InitializeStorageAsync(ServiceProvider sp);

    protected static string NewId() => Guid.NewGuid().ToString("N");

    // ---------------------------------------------------------------- events

    [Fact]
    public async Task Save_and_load_roundtrip_restores_state_and_version()
    {
        await using var sp = BuildHost("roundtrip");
        await InitializeStorageAsync(sp);
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
    public async Task Load_unknown_id_returns_null()
    {
        await using var sp = BuildHost("unknown");
        await InitializeStorageAsync(sp);
        var store = sp.GetRequiredService<IEventStore<Order, string>>();

        (await store.LoadByIdAsync(NewId())).Should().BeNull();
    }

    [Fact]
    public async Task Event_metadata_roundtrips()
    {
        await using var sp = BuildHost("metadata");
        await InitializeStorageAsync(sp);
        var store = sp.GetRequiredService<IEventStore<Order, string>>();
        var id = NewId();
        var context = TestHost.NewCommandContext() with { Headers = new Dictionary<string, string> { ["k"] = "v" } };

        var order = Order.CreateNew(id);
        order.Create("ACME");
        await store.SaveAsync(order, context);

        var envelope = (await store.LoadEventsAsync(id)).Should().ContainSingle().Subject;
        envelope.Payload.Should().BeOfType<OrderCreated>().Which.Customer.Should().Be("ACME");
        envelope.Metadata.AggregateId.Should().Be(id);
        envelope.Metadata.AggregateType.Should().Be(nameof(Order));
        envelope.Metadata.Version.Should().Be(0);
        envelope.Metadata.CorrelationId.Should().Be(context.CorrelationId);
        envelope.Metadata.CausationId.Should().Be(context.CommandId.ToString());
        envelope.Metadata.TenantId.Should().Be(context.TenantId);
        envelope.Metadata.UserId.Should().Be(context.UserId);
        envelope.Metadata.Headers.Should().ContainKey("k").WhoseValue.Should().Be("v");
        envelope.Metadata.OccurredAt.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public async Task Multiple_saves_append_to_the_same_stream_and_load_events_from_version()
    {
        await using var sp = BuildHost("append");
        await InitializeStorageAsync(sp);
        var store = sp.GetRequiredService<IEventStore<Order, string>>();
        var id = NewId();

        var order = Order.CreateNew(id);
        order.Create("ACME");
        await store.SaveAsync(order, TestHost.NewCommandContext());
        order.AddItem("A", 1m);
        order.AddItem("B", 2m);
        await store.SaveAsync(order, TestHost.NewCommandContext());

        (await store.LoadEventsAsync(id)).Should().HaveCount(3);
        (await store.LoadEventsAsync(id, fromVersion: 1)).Select(e => e.Metadata.Version).Should().Equal(1, 2);
        (await store.LoadByIdAsync(id))!.Version.Should().Be(2);
    }

    [Fact]
    public async Task IsExists_and_GetAllIds_reflect_saved_aggregates()
    {
        await using var sp = BuildHost("exists");
        await InitializeStorageAsync(sp);
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
    public async Task Events_with_a_configured_name_roundtrip()
    {
        // Contract for every persistence: [EventName] is the storage name, and resolving
        // back to the CLR type must work through it (not through the type name).
        await using var sp = BuildHost("event_name");
        await InitializeStorageAsync(sp);
        var store = sp.GetRequiredService<IEventStore<Order, string>>();
        var id = NewId();

        var order = Order.CreateNew(id);
        order.Create("ACME");
        order.Rename("ACME GmbH");
        await store.SaveAsync(order, TestHost.NewCommandContext());

        var loaded = await store.LoadByIdAsync(id);

        loaded!.Customer.Should().Be("ACME GmbH");
        (await store.LoadEventsAsync(id)).Select(e => e.Payload.GetType())
            .Should().Equal([typeof(OrderCreated), typeof(OrderRenamed)],
                "named and unnamed events must work side by side in the same stream");
    }

    // ---------------------------------------------------------------- concurrency

    [Fact]
    public async Task Stale_writer_gets_concurrency_exception_and_nothing_is_written()
    {
        await using var sp = BuildHost("concurrency");
        await InitializeStorageAsync(sp);
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
        await ((Func<Task>)(() => store.SaveAsync(copy2, TestHost.NewCommandContext())))
            .Should().ThrowAsync<ConcurrencyException<string>>();

        var events = await store.LoadEventsAsync(id);
        events.Should().HaveCount(2, "the losing writer must not leave partial data behind");
    }

    [Fact]
    public async Task Concurrent_creation_of_the_same_new_aggregate_yields_exactly_one_winner()
    {
        await using var sp = BuildHost("create_race");
        await InitializeStorageAsync(sp);
        var store = sp.GetRequiredService<IEventStore<Order, string>>();
        var id = NewId();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            var order = Order.CreateNew(id);
            order.Create($"writer-{i}");
            try { await store.SaveAsync(order, TestHost.NewCommandContext()); return true; }
            catch (ConcurrencyException<string>) { return false; }
        }));

        results.Count(r => r).Should().Be(1);
        (await store.LoadEventsAsync(id)).Should().ContainSingle();
    }

    // ---------------------------------------------------------------- snapshots

    [Fact]
    public async Task Snapshot_plus_delta_replays_only_the_delta()
    {
        await using var sp = BuildHost("snapshots");
        await InitializeStorageAsync(sp);
        var store = sp.GetRequiredService<IEventStore<Customer, string>>();
        var id = NewId();

        var customer = Customer.CreateNew(id);
        customer.Register("ACME GmbH", CustomerKind.Company, DateTime.UtcNow);
        customer.Relocate("Hauptstraße 1", "Berlin");
        await store.SaveAsync(customer, TestHost.NewCommandContext());   // v1 → snapshot (N=2)
        customer.AddContact("Alex", "alex@acme.test");
        await store.SaveAsync(customer, TestHost.NewCommandContext());   // v2
        customer.AddContact("Kim", "kim@acme.test");
        await store.SaveAsync(customer, TestHost.NewCommandContext());   // v3 → snapshot

        var loaded = await store.LoadByIdAsync(id);

        loaded!.Version.Should().Be(3);
        loaded.Name.Should().Be("ACME GmbH");
        loaded.Address!.City.Should().Be("Berlin");
        loaded.Contacts.Select(c => c.Name).Should().BeEquivalentTo("Alex", "Kim");

        loaded.AddContact("Sam", "sam@acme.test");
        await store.SaveAsync(loaded, TestHost.NewCommandContext());     // no conflict after snapshot load
        (await store.LoadByIdAsync(id))!.Contacts.Should().HaveCount(3);
    }

    // ---------------------------------------------------------------- outbox

    [Fact]
    public async Task Outbox_delivers_to_subscribers_and_tracks_partial_failure_per_subscriber()
    {
        await using var sp = BuildHost("outbox", withOutbox: true);
        await InitializeStorageAsync(sp);
        var store = sp.GetRequiredService<IEventStore<Order, string>>();
        var worker = sp.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<OutboxWorker>().Single();
        ContractFlakySubscriber.FailuresRemaining = 1;

        var customer = $"outbox-{NewId()}";
        var order = Order.CreateNew(NewId());
        order.Create(customer);
        await store.SaveAsync(order, TestHost.NewCommandContext());

        for (var i = 0; i < 4; i++)
            await worker.ProcessBatchAsync(CancellationToken.None);

        EventRecorder.AsyncHandled.Where(c => c == customer).Should().ContainSingle("default subscriber gets it exactly once");
        ContractRecordingSubscriber.Delivered.Where(c => c == customer).Should().ContainSingle();
        ContractFlakySubscriber.Attempts.Should().BeGreaterThanOrEqualTo(2, "flaky subscriber is retried");
        ContractFlakySubscriber.Succeeded.Should().Contain(customer);
    }

    [Fact]
    public async Task Dequeue_delivers_envelopes_strictly_oldest_first()
    {
        await using var sp = BuildHost("outbox_order", withOutbox: true);
        await InitializeStorageAsync(sp);
        var outbox = sp.GetRequiredService<IOutboxPersistence>();
        var names = new[] { "order-check" };

        var enqueued = new List<Guid>();
        for (var i = 0; i < 12; i++)
        {
            var id = Guid.NewGuid();
            enqueued.Add(id);
            await outbox.EnqueueAsync(BuildEnvelope(id, i));
            await Task.Delay(5); // strikt aufsteigende CreatedAt-Zeitstempel
        }

        var delivered = new List<Guid>();
        for (var round = 0; round < 10 && delivered.Count < enqueued.Count; round++)
        {
            // small batches force several dequeue rounds
            var batch = await outbox.DequeueBatchAsync(names, 5);
            foreach (var pending in batch)
            {
                delivered.Add(pending.Envelope.Metadata.EventId);
                await outbox.MarkDispatchedAsync(pending.Envelope, "order-check", names);
            }
        }

        delivered.Should().Equal(enqueued,
            "the dequeue must deliver strictly oldest first (FIFO by CreatedAt), across batch boundaries too");
    }

    private static OutboxEnvelope BuildEnvelope(Guid eventId, int sequence) => new(
        new OutboxPayload("test-event", "{}"),
        new OutboxMetadata(eventId, "OrderTest", $"agg-{sequence}", sequence, DateTime.UtcNow,
            null, null, Guid.NewGuid().ToString("N"), null, null));

    // ---------------------------------------------------------------- test subscribers (shared)

    public sealed class ContractRecordingSubscriber : IOutboxSubscriber
    {
        public static System.Collections.Concurrent.ConcurrentBag<string> Delivered { get; } = [];
        public string Name => "contract-recording";
        public Task DispatchAsync(OutboxEnvelope envelope, CancellationToken cancellationToken = default)
        {
            Delivered.Add(ExtractCustomer(envelope));
            return Task.CompletedTask;
        }
    }

    public sealed class ContractFlakySubscriber : IOutboxSubscriber
    {
        public static int FailuresRemaining;
        public static int Attempts;
        public static System.Collections.Concurrent.ConcurrentBag<string> Succeeded { get; } = [];
        public string Name => "contract-flaky";
        public Task DispatchAsync(OutboxEnvelope envelope, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Attempts);
            if (Interlocked.Decrement(ref FailuresRemaining) >= 0)
                throw new InvalidOperationException("broker down");
            Succeeded.Add(ExtractCustomer(envelope));
            return Task.CompletedTask;
        }
    }

    private static string ExtractCustomer(OutboxEnvelope envelope)
    {
        var type = EventTypeNames.ResolveRequired(envelope.Payload.EventType);
        return ((OrderCreated)System.Text.Json.JsonSerializer.Deserialize(envelope.Payload.Data, type)!).Customer;
    }
}
