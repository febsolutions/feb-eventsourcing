using FEB.EventSourcing.InMemory;
using FEB.EventSourcing.Redis;
using FEB.EventSourcing.Snapshots;
using FEB.EventSourcing.Tests.Infrastructure;
using FEB.EventSourcing.Tests.TestDomain;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace FEB.EventSourcing.Tests.Redis;

[Collection("redis")]
public class RedisIntegrationTests(RedisFixture fixture)
{
    private RedisCacheDatabase BuildDatabase(string prefix)
    {
        var options = new RedisEventStoreOptions();
        options.SetConnectionString(fixture.ConnectionString);
        options.SetCachePrefix(prefix);
        return new RedisCacheDatabase(options);
    }

    private static CachedAggregateState State(int version, byte[]? payload = null)
        => new(payload ?? [1, 2, 3], version, 42, SnapshotCompression.None, DateTime.UtcNow);

    [Fact]
    public async Task Cas_write_rejects_older_versions()
    {
        var db = BuildDatabase(NewPrefix());

        (await db.TrySetAsync("k", State(5), TimeSpan.FromMinutes(1))).Should().BeTrue();
        (await db.TrySetAsync("k", State(3), TimeSpan.FromMinutes(1)))
            .Should().BeFalse("an older version must never overwrite a newer one");
        (await db.TrySetAsync("k", State(5), TimeSpan.FromMinutes(1)))
            .Should().BeFalse("the same version is no progress");
        (await db.TrySetAsync("k", State(6, [9, 9]), TimeSpan.FromMinutes(1))).Should().BeTrue();

        var current = await db.GetAsync("k");
        current!.StreamVersion.Should().Be(6);
        current.Payload.Should().Equal(9, 9);
        current.SchemaVersion.Should().Be(42);
    }

    [Fact]
    public async Task Entries_expire_via_ttl()
    {
        var db = BuildDatabase(NewPrefix());

        await db.TrySetAsync("k", State(1), TimeSpan.FromMilliseconds(300));
        (await db.ExistsAsync("k")).Should().BeTrue();

        await Task.Delay(1000);

        (await db.GetAsync("k")).Should().BeNull("the entry must expire via TTL");
    }

    [Fact]
    public async Task Delete_removes_entry()
    {
        var db = BuildDatabase(NewPrefix());

        await db.TrySetAsync("k", State(1), TimeSpan.FromMinutes(1));
        await db.DeleteAsync("k");

        (await db.ExistsAsync("k")).Should().BeFalse();
    }

    [Fact]
    public async Task Full_chain_second_load_hits_redis_cache()
    {
        var metrics = new RecordingMetrics();
        await using var sp = BuildHost(NewPrefix(), metrics);

        var store = sp.GetRequiredService<IEventStore<Customer, string>>();
        var id = Guid.NewGuid().ToString("N");

        var customer = Customer.CreateNew(id);
        customer.Register("ACME GmbH", CustomerKind.Company, DateTime.UtcNow);
        customer.Relocate("Hauptstraße 1", "Berlin");
        await store.SaveAsync(customer, TestHost.NewCommandContext());

        var loaded = await store.LoadByIdAsync(id);

        loaded.Should().NotBeNull();
        loaded!.Version.Should().Be(1);
        loaded.Address!.City.Should().Be("Berlin");
        metrics.Reads.Should().Contain("redis", "the load after the save must come from the cache");
    }

    [Fact]
    public async Task Full_chain_stale_writer_conflicts_and_cache_recovers()
    {
        await using var sp = BuildHost(NewPrefix(), new RecordingMetrics());

        var store = sp.GetRequiredService<IEventStore<Customer, string>>();
        var id = Guid.NewGuid().ToString("N");

        var customer = Customer.CreateNew(id);
        customer.Register("ACME GmbH", CustomerKind.Company, DateTime.UtcNow);
        await store.SaveAsync(customer, TestHost.NewCommandContext());

        var copy1 = await store.LoadByIdAsync(id);
        var copy2 = await store.LoadByIdAsync(id);

        copy1!.AddContact("Alex", "alex@acme.test");
        await store.SaveAsync(copy1, TestHost.NewCommandContext());

        copy2!.AddContact("Kim", "kim@acme.test");
        var act = () => store.SaveAsync(copy2, TestHost.NewCommandContext());
        await act.Should().ThrowAsync<ConcurrencyException<string>>();

        var reloaded = await store.LoadByIdAsync(id);
        reloaded!.Version.Should().Be(1);
        reloaded.Contacts.Select(c => c.Name).Should().BeEquivalentTo("Alex");
    }

    private ServiceProvider BuildHost(string prefix, IEventStoreMetrics metrics)
    {
        var services = new ServiceCollection();

        services.AddEventSourcing(es =>
        {
            es.UseInMemory();
            es.UseSnapshots();
            es.UseRedis(fixture.ConnectionString, o =>
            {
                o.SetCachePrefix(prefix);
                o.SetTtlJitter(0);
            });

            es.Aggregate<Customer, string>(a => a.Factory(Customer.CreateNew));
        }, typeof(TestHost).Assembly);

        services.AddSingleton(metrics);

        return services.BuildServiceProvider();
    }

    private static string NewPrefix() => $"t{Guid.NewGuid():N}";
}
