using FEB.EventSourcing.Redis;
using FEB.EventSourcing.Snapshots;
using FEB.EventSourcing.Snapshots.Generated;
using FEB.EventSourcing.Tests.TestDomain;
using AwesomeAssertions;

namespace FEB.EventSourcing.Tests.Redis;

public class RedisCacheStoreTests
{
    private static readonly SnapshotMetadataRegistry Registry =
        new(SnapshotMetadataModule_FEB_EventSourcing_Tests.CreateAll());

    private static Customer BuildCustomer(string id, int version = 2)
    {
        var customer = Customer.CreateNew(id);
        customer.Register("ACME GmbH", CustomerKind.Company, DateTime.UtcNow);
        customer.Relocate("Hauptstraße 1", "Berlin");
        customer.AddContact("Alex", "alex@acme.test");
        customer.Commit(version);
        return customer;
    }

    private static (RedisCacheStore<Customer, string> Store, CountingInnerStore<Customer, string> Inner, FakeRedisCacheDatabase Redis, RecordingMetrics Metrics)
        BuildStore(Func<string, Customer?> loader)
    {
        var inner = new CountingInnerStore<Customer, string>(loader);
        var redis = new FakeRedisCacheDatabase();
        var metrics = new RecordingMetrics();
        var options = new RedisEventStoreOptions();

        var store = new RedisCacheStore<Customer, string>(
            inner,
            new AggregateFactory<Customer, string>(Customer.CreateNew),
            Registry,
            redis,
            new RedisSnapshotSerializer(),
            options,
            metrics);

        return (store, inner, redis, metrics);
    }

    [Fact]
    public async Task Cache_miss_loads_from_inner_store_and_populates_cache()
    {
        var (store, inner, redis, _) = BuildStore(id => BuildCustomer(id));

        var loaded = await store.LoadByIdAsync("cust-1");

        loaded.Should().NotBeNull();
        inner.LoadCalls.Should().Be(1);
        redis.Store.Should().ContainKey("Customer:cust-1", "cache-aside must fill the cache on a miss");
    }

    [Fact]
    public async Task Second_load_is_served_from_cache_without_inner_store()
    {
        var (store, inner, _, metrics) = BuildStore(id => BuildCustomer(id));

        await store.LoadByIdAsync("cust-1");
        var second = await store.LoadByIdAsync("cust-1");

        inner.LoadCalls.Should().Be(1, "the second load must come from the cache");
        metrics.Reads.Should().Contain("redis");

        second!.Version.Should().Be(2);
        second.Name.Should().Be("ACME GmbH");
        second.Address!.City.Should().Be("Berlin");
        second.Contacts.Should().ContainSingle();
        second.Category.Should().BeNull();
    }

    [Fact]
    public async Task Save_writes_through_to_cache_with_new_version()
    {
        var (store, _, redis, _) = BuildStore(id => null);

        var customer = BuildCustomer("cust-1", version: -1);
        await store.SaveAsync(customer, Infrastructure.TestHost.NewCommandContext());

        redis.Store["Customer:cust-1"].StreamVersion.Should().Be(customer.Version);
    }

    [Fact]
    public async Task Concurrency_conflict_invalidates_cache_and_rethrows()
    {
        var (store, inner, redis, _) = BuildStore(id => BuildCustomer(id));

        await store.LoadByIdAsync("cust-1");
        redis.Store.Should().ContainKey("Customer:cust-1");

        inner.ThrowOnSave = new ConcurrencyException<string>("cust-1", 2);
        var stale = BuildCustomer("cust-1");
        stale.AddContact("Kim", "kim@acme.test");

        var act = () => store.SaveAsync(stale, Infrastructure.TestHost.NewCommandContext());

        await act.Should().ThrowAsync<ConcurrencyException<string>>();
        redis.Store.Should().NotContainKey("Customer:cust-1",
            "after a conflict no potentially stale entry may be served any more");
    }

    [Fact]
    public async Task Schema_version_mismatch_is_a_cache_miss()
    {
        var (store, inner, redis, _) = BuildStore(id => BuildCustomer(id));

        await store.LoadByIdAsync("cust-1");
        var poisoned = redis.Store["Customer:cust-1"];
        redis.Store["Customer:cust-1"] = poisoned with { SchemaVersion = poisoned.SchemaVersion + 1 };

        await store.LoadByIdAsync("cust-1");

        inner.LoadCalls.Should().Be(2, "on a schema mismatch the inner store must be loaded");
    }

    [Fact]
    public async Task Corrupt_cache_payload_falls_back_to_inner_store()
    {
        var (store, inner, redis, _) = BuildStore(id => BuildCustomer(id));

        await store.LoadByIdAsync("cust-1");
        var poisoned = redis.Store["Customer:cust-1"];
        redis.Store["Customer:cust-1"] = poisoned with { Payload = [1, 2, 3] };

        var loaded = await store.LoadByIdAsync("cust-1");

        loaded.Should().NotBeNull();
        inner.LoadCalls.Should().Be(2);
    }

    [Fact]
    public async Task Aggregate_without_snapshot_metadata_passes_through()
    {
        var inner = new CountingInnerStore<Order, string>(_ => null);
        var redis = new FakeRedisCacheDatabase();

        var store = new RedisCacheStore<Order, string>(
            inner,
            new AggregateFactory<Order, string>(Order.CreateNew),
            Registry,
            redis,
            new RedisSnapshotSerializer(),
            new RedisEventStoreOptions(),
            new RecordingMetrics());

        await store.LoadByIdAsync("order-1");
        await store.IsExistsAsync("order-1");

        inner.LoadCalls.Should().Be(1);
        redis.Store.Should().BeEmpty("without [AutoSnapshot] Redis must not be touched");
    }

    [Fact]
    public async Task IsExists_uses_cache_before_inner_store()
    {
        var (store, inner, _, _) = BuildStore(id => BuildCustomer(id));

        await store.LoadByIdAsync("cust-1");

        (await store.IsExistsAsync("cust-1")).Should().BeTrue();
        inner.LoadCalls.Should().Be(1);
    }
}
