using System.Collections.Concurrent;
using FEB.EventSourcing.MongoDb;
using FEB.EventSourcing.Tests.Infrastructure;
using FEB.EventSourcing.Tests.TestDomain;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace FEB.EventSourcing.Tests.MongoDb;

[Collection("mongo")]
public class OutboxTests(MongoDbFixture fixture)
{
    private const string Handlers = DefaultOutboxDispatcher.SubscriberName;

    private (ServiceProvider Provider, IMongoDatabase Db) BuildHost(string database, int maxAttempts = 10, Action<IEventSourcingBuilder>? extra = null)
    {
        var services = new ServiceCollection();
        var connectionString = fixture.GetConnectionString(database);

        services.AddEventSourcing(es =>
        {
            es.UseMongoDb(connectionString, o => o.DisableIndexInitialization());
            es.UseMongoOutbox(o => o.SetMaxAttempts(maxAttempts));
            extra?.Invoke(es);
            es.UseOutboxWorker(o => o.SetPollingInterval(TimeSpan.FromMilliseconds(50)));

            es.Aggregate<Order, string>(a => a.Factory(Order.CreateNew));
        }, typeof(TestHost).Assembly);

        var sp = services.BuildServiceProvider();
        var db = new MongoClient(connectionString).GetDatabase(new MongoUrl(connectionString).DatabaseName);
        return (sp, db);
    }

    private static OutboxWorker GetWorker(ServiceProvider sp)
        => sp.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<OutboxWorker>().Single();

    private static async Task<string> SaveOrderAsync(ServiceProvider sp, string prefix)
    {
        var store = sp.GetRequiredService<IEventStore<Order, string>>();
        var customer = $"{prefix}-{Guid.NewGuid():N}";
        var order = Order.CreateNew(Guid.NewGuid().ToString("N"));
        order.Create(customer);
        await store.SaveAsync(order, TestHost.NewCommandContext());
        return customer;
    }

    private static async Task DrainAsync(OutboxWorker worker, int rounds = 5)
    {
        for (var i = 0; i < rounds; i++)
            await worker.ProcessBatchAsync(CancellationToken.None);
    }

    // ---------------------------------------------------------------- default (single subscriber)

    [Fact]
    public async Task Default_worker_delivers_to_async_handlers()
    {
        var (sp, _) = BuildHost("es_outbox");
        await using var _ = sp;

        var customer = await SaveOrderAsync(sp, "outbox");
        await DrainAsync(GetWorker(sp));

        EventRecorder.AsyncHandled.Should().Contain(customer,
            "the outbox must deliver events with their complete payload to IASyncEventHandlers");
    }

    [Fact]
    public async Task Envelopes_carry_the_configured_event_name_and_still_reach_handlers()
    {
        var (sp, db) = BuildHost("es_outbox_evname");
        await using var _ = sp;

        var store = sp.GetRequiredService<IEventStore<Order, string>>();
        var customer = $"renamed-{Guid.NewGuid():N}";
        var order = Order.CreateNew(Guid.NewGuid().ToString("N"));
        order.Rename(customer);
        await store.SaveAsync(order, TestHost.NewCommandContext());

        var envelope = await db.GetCollection<OutboxDao>("outbox")
            .Find(Builders<OutboxDao>.Filter.Empty).SortByDescending(x => x.CreatedAt).Limit(1).SingleAsync();
        System.Text.Json.JsonSerializer.Deserialize<OutboxPayload>(envelope.PayloadJson)!
            .EventType.Should().Be("order.renamed",
            "the outbox payload must carry the stable name too - subscribers and brokers see it");

        await DrainAsync(GetWorker(sp));

        EventRecorder.AsyncHandled.Should().Contain(customer,
            "the default dispatcher must resolve the stable name back to the CLR type");
    }

    [Fact]
    public async Task Dispatched_envelopes_are_completed_and_not_delivered_twice()
    {
        var (sp, db) = BuildHost("es_outbox_once");
        await using var _ = sp;

        var customer = await SaveOrderAsync(sp, "once");
        var worker = GetWorker(sp);

        await DrainAsync(worker);
        await DrainAsync(worker);

        EventRecorder.AsyncHandled.Where(c => c == customer).Should().ContainSingle();

        var dao = await db.GetCollection<OutboxDao>("outbox").Find(_ => true).SortByDescending(d => d.CreatedAt).FirstAsync();
        dao.DispatchedAt.Should().NotBeNull("the envelope must be completed after delivery to all subscribers");
        dao.Deliveries![Handlers].DispatchedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Failed_delivery_moves_subscription_to_dead_letter_after_max_attempts()
    {
        var (sp, db) = BuildHost("es_outbox_dlq", maxAttempts: 2, extra: es => es.UseOutboxSubscriber<AlwaysFailingSubscriber>());
        await using var _ = sp;

        await SaveOrderAsync(sp, "dlq");
        await DrainAsync(GetWorker(sp), rounds: 6);

        var dlq = await db.GetCollection<OutboxDeadLetterDao>("outbox_deadletter").Find(_ => true).ToListAsync();
        dlq.Should().ContainSingle(d => d.Subscriber == AlwaysFailingSubscriber.SubscriberName,
            "after MaxAttempts exactly this subscription moves to the dead-letter queue");

        var dao = await db.GetCollection<OutboxDao>("outbox").Find(_ => true).FirstAsync();
        dao.Deliveries![AlwaysFailingSubscriber.SubscriberName].DeadLetteredAt.Should().NotBeNull();
        dao.Deliveries[AlwaysFailingSubscriber.SubscriberName].AttemptCount.Should().Be(2);
        dao.DispatchedAt.Should().NotBeNull("all known subscribers are terminal (dead-lettered counts)");
    }

    // ---------------------------------------------------------------- multiple subscribers

    [Fact]
    public async Task One_failing_subscriber_does_not_cause_redelivery_to_the_others()
    {
        var (sp, db) = BuildHost("es_outbox_multi", maxAttempts: 3, extra: es =>
        {
            es.UseDefaultOutboxSubscriber();
            es.UseOutboxSubscriber<RecordingSubscriber>();
            es.UseOutboxSubscriber<FlakySubscriber>();
        });
        await using var _ = sp;
        FlakySubscriber.FailuresRemaining = 2;

        var customer = await SaveOrderAsync(sp, "multi");
        var worker = GetWorker(sp);

        // Runde 1: handlers ok, recording ok, flaky fail (1)
        // Round 2: only the flaky subscriber is retried -> fail (2)
        // Runde 3: flaky ok → Envelope komplett
        await DrainAsync(worker, rounds: 4);

        EventRecorder.AsyncHandled.Where(c => c == customer).Should().ContainSingle("the default subscriber must be delivered to only once");
        RecordingSubscriber.Delivered.Where(c => c == customer).Should().ContainSingle("subscribers that did not fail must not be delivered to again");
        FlakySubscriber.Attempts.Should().Be(3, "the failed subscriber is retried until it succeeds");

        var dao = await db.GetCollection<OutboxDao>("outbox").Find(_ => true).FirstAsync();
        dao.Deliveries.Should().HaveCount(3);
        dao.Deliveries!.Values.Should().OnlyContain(d => d.DispatchedAt != null);
        dao.DispatchedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Legacy_envelope_without_deliveries_is_delivered_to_all_subscribers_once()
    {
        var (sp, db) = BuildHost("es_outbox_legacy", extra: es =>
        {
            es.UseDefaultOutboxSubscriber();
            es.UseOutboxSubscriber<RecordingSubscriber>();
        });
        await using var _ = sp;

        var customer = await SaveOrderAsync(sp, "legacy");

        // Turn the envelope back into the old format (no Deliveries field)
        var outbox = db.GetCollection<OutboxDao>("outbox");
        var dao = await outbox.Find(_ => true).SortByDescending(d => d.CreatedAt).FirstAsync();
        await outbox.UpdateOneAsync(d => d.Id == dao.Id, Builders<OutboxDao>.Update.Unset(d => d.Deliveries));

        await DrainAsync(GetWorker(sp));

        EventRecorder.AsyncHandled.Should().Contain(customer);
        RecordingSubscriber.Delivered.Should().Contain(customer);
        (await outbox.Find(d => d.Id == dao.Id).FirstAsync()).DispatchedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Legacy_dispatcher_registration_still_works_as_single_subscriber()
    {
        var (sp, _) = BuildHost("es_outbox_dispatcher", extra: es => es.UseOutboxDispatcher<RecordingDispatcher>());
        await using var _ = sp;

        var customer = await SaveOrderAsync(sp, "disp");
        await DrainAsync(GetWorker(sp));

        RecordingDispatcher.Delivered.Should().Contain(customer);
        EventRecorder.AsyncHandled.Should().NotContain(customer, "UseOutboxDispatcher replaces the default subscriber");
    }

    // ---------------------------------------------------------------- test subscribers

    public sealed class RecordingSubscriber : IOutboxSubscriber
    {
        public static ConcurrentBag<string> Delivered { get; } = [];
        public string Name => "recording";

        public Task DispatchAsync(OutboxEnvelope envelope, CancellationToken cancellationToken = default)
        {
            Delivered.Add(ExtractCustomer(envelope));
            return Task.CompletedTask;
        }
    }

    public sealed class FlakySubscriber : IOutboxSubscriber
    {
        public static int FailuresRemaining;
        public static int Attempts;
        public string Name => "flaky";

        public Task DispatchAsync(OutboxEnvelope envelope, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Attempts);
            if (Interlocked.Decrement(ref FailuresRemaining) >= 0)
                throw new InvalidOperationException("broker down");
            return Task.CompletedTask;
        }
    }

    public sealed class AlwaysFailingSubscriber : IOutboxSubscriber
    {
        public const string SubscriberName = "broken";
        public string Name => SubscriberName;

        public Task DispatchAsync(OutboxEnvelope envelope, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("permanently broken");
    }

    public sealed class RecordingDispatcher : IOutboxDispatcher
    {
        public static ConcurrentBag<string> Delivered { get; } = [];

        public Task DispatchAsync(OutboxEnvelope envelope, CancellationToken cancellationToken = default)
        {
            Delivered.Add(ExtractCustomer(envelope));
            return Task.CompletedTask;
        }
    }

    private static string ExtractCustomer(OutboxEnvelope envelope)
    {
        var type = EventTypeNames.ResolveRequired(envelope.Payload.EventType);
        var evt = (OrderCreated)System.Text.Json.JsonSerializer.Deserialize(envelope.Payload.Data, type)!;
        return evt.Customer;
    }
}
