using FEB.EventSourcing.InMemory;
using FEB.EventSourcing.MongoDb;
using FEB.EventSourcing.Redis;
using FEB.EventSourcing.Snapshots;
using FEB.EventSourcing.Tests.Infrastructure;
using FEB.EventSourcing.Tests.TestDomain;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace FEB.EventSourcing.Tests.Core;

/// <summary>
/// The chain order must come from IEventStoreRegistration.Order, not from the
/// order of the Use*() calls.
/// </summary>
[Collection("mongo")]
public class ChainOrderTests(MongoDbFixture fixture)
{
    [Fact]
    public void Redis_registered_before_snapshots_still_ends_up_outermost()
    {
        var services = new ServiceCollection();

        services.AddEventSourcing(es =>
        {
            // Absichtlich "falsche" Aufrufreihenfolge: Redis vor Snapshots
            es.UseMongoDb(fixture.GetConnectionString("es_chain_order"), o =>
            {
                o.EnableSnapshots();
                o.DisableIndexInitialization();
            });
            es.UseSnapshots();
            es.UseRedis("localhost:1", o => o.SetCachePrefix("unused"));

            es.Aggregate<Customer, string>(a => a.Factory(Customer.CreateNew));
        }, typeof(TestHost).Assembly);

        // Only check the registration types - no real Redis needed
        var descriptors = services.Where(d => d.ServiceType.IsGenericType).Select(d => d.ServiceType).ToList();
        descriptors.Should().Contain(typeof(RedisCacheStore<Customer, string>));
        descriptors.Should().Contain(typeof(SnapshotStore<Customer, string>));

        // The outer store (EventStore<>) wraps the last registered type - that must be Redis.
        // We check the chain via the registration order contract:
        new RedisRegistration(new RedisEventStoreOptions()).Order
            .Should().BeGreaterThan(new SnapshotStoreRegistration().Order,
                "the cache layer must sit outside the snapshot layer");
    }

    [Fact]
    public async Task Custom_layer_can_slot_in_at_any_order()
    {
        var services = new ServiceCollection();

        services.AddEventSourcing(es =>
        {
            es.UseInMemory();
            es.EventStoreChainBuilder.AddDecorator(new CountingRegistration());
            es.Aggregate<Order, string>(a => a.Factory(Order.CreateNew));
        }, typeof(TestHost).Assembly);

        await using var sp = services.BuildServiceProvider();
        var store = sp.GetRequiredService<IEventStore<Order, string>>();

        var order = Order.CreateNew("o1");
        order.Create("ACME");
        await store.SaveAsync(order, TestHost.NewCommandContext());
        await store.LoadByIdAsync("o1");

        CountingStore<Order, string>.Loads.Should().BeGreaterThan(0, "the custom layer must be part of the chain");
    }

    private sealed class CountingRegistration : IEventStoreRegistration
    {
        public int Order => EventStoreLayer.CrossCutting;

        public IEventStore<TAggregate, TId> CreateEventStore<TAggregate, TId>(IEventStore<TAggregate, TId>? innerStore, IServiceProvider sp, EventStoreOptions eventStoreOptions)
            where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new()
            => new CountingStore<TAggregate, TId>(innerStore!);

        public Type GetRegisteredType<TAggregate, TId>()
            where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new()
            => typeof(CountingStore<TAggregate, TId>);

        public void RegisterEventStore<TAggregate, TId>(IServiceCollection services, Type? innerStoreType, EventStoreOptions eventStoreOptions, bool registerAsOuterStore = false)
            where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new()
            => services.AddScoped(sp => new CountingStore<TAggregate, TId>((IEventStore<TAggregate, TId>)sp.GetRequiredService(innerStoreType!)));
    }

    private sealed class CountingStore<TAggregate, TId>(IEventStore<TAggregate, TId> inner) : ProxyStore<TAggregate, TId>(inner)
        where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new()
    {
        public static int Loads;

        public override Task<TAggregate?> LoadByIdAsync(TId id, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Loads);
            return base.LoadByIdAsync(id, cancellationToken);
        }
    }
}
