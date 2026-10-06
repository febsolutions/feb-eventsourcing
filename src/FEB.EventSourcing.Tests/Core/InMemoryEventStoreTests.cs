using FEB.EventSourcing.Tests.Infrastructure;
using FEB.EventSourcing.Tests.TestDomain;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace FEB.EventSourcing.Tests.Core;

public class InMemoryEventStoreTests
{
    [Fact]
    public async Task Save_and_load_roundtrip_restores_state_and_version()
    {
        await using var sp = TestHost.BuildInMemory();
        var store = sp.GetRequiredService<IEventStore<Order, string>>();

        var order = Order.CreateNew("order-1");
        order.Create("ACME");
        order.AddItem("Widget", 9.99m);
        await store.SaveAsync(order, TestHost.NewCommandContext());

        order.Version.Should().Be(1, "two events from -1 result in version 1");
        order.GetUncommittedEvents().Should().BeEmpty();

        var loaded = await store.LoadByIdAsync("order-1");

        loaded.Should().NotBeNull();
        loaded!.Version.Should().Be(1);
        loaded.Customer.Should().Be("ACME");
        loaded.Items.Should().ContainSingle().Which.Should().Be("Widget");
        loaded.Total.Should().Be(9.99m);
    }

    [Fact]
    public async Task Load_unknown_id_returns_null()
    {
        await using var sp = TestHost.BuildInMemory();
        var store = sp.GetRequiredService<IEventStore<Order, string>>();

        var loaded = await store.LoadByIdAsync("does-not-exist");

        loaded.Should().BeNull();
    }

    [Fact]
    public async Task Save_without_uncommitted_events_is_a_noop()
    {
        await using var sp = TestHost.BuildInMemory();
        var store = sp.GetRequiredService<IEventStore<Order, string>>();

        var order = Order.CreateNew("order-1");
        await store.SaveAsync(order, TestHost.NewCommandContext());

        (await store.IsExistsAsync("order-1")).Should().BeFalse();
    }

    [Fact]
    public async Task IsExists_and_GetAllIds_reflect_saved_aggregates()
    {
        await using var sp = TestHost.BuildInMemory();
        var store = sp.GetRequiredService<IEventStore<Order, string>>();

        foreach (var id in new[] { "a", "b" })
        {
            var order = Order.CreateNew(id);
            order.Create("Customer " + id);
            await store.SaveAsync(order, TestHost.NewCommandContext());
        }

        (await store.IsExistsAsync("a")).Should().BeTrue();
        (await store.IsExistsAsync("c")).Should().BeFalse();
        (await store.GetAllIdsAsync()).Should().BeEquivalentTo("a", "b");
    }

    [Fact]
    public async Task Multiple_saves_append_to_the_same_stream()
    {
        await using var sp = TestHost.BuildInMemory();
        var store = sp.GetRequiredService<IEventStore<Order, string>>();

        var order = Order.CreateNew("order-1");
        order.Create("ACME");
        await store.SaveAsync(order, TestHost.NewCommandContext());

        order.AddItem("Widget", 1m);
        await store.SaveAsync(order, TestHost.NewCommandContext());

        var loaded = await store.LoadByIdAsync("order-1");
        loaded!.Version.Should().Be(1);
        loaded.Items.Should().ContainSingle();
    }

    [Fact]
    public async Task Load_events_returns_envelopes_with_metadata_in_order()
    {
        await using var sp = TestHost.BuildInMemory();
        var store = sp.GetRequiredService<IEventStore<Order, string>>();

        var context = TestHost.NewCommandContext();
        var order = Order.CreateNew("order-1");
        order.Create("ACME");
        order.AddItem("Widget", 1m);
        await store.SaveAsync(order, context);

        var envelopes = await store.LoadEventsAsync("order-1");

        envelopes.Should().HaveCount(2);
        envelopes.Select(e => e.Metadata.Version).Should().ContainInOrder(0, 1);
        envelopes.Should().AllSatisfy(e =>
        {
            e.Metadata.AggregateId.Should().Be("order-1");
            e.Metadata.AggregateType.Should().Be(nameof(Order));
            e.Metadata.CorrelationId.Should().Be(context.CorrelationId);
            e.Metadata.CausationId.Should().Be(context.CommandId.ToString());
            e.Metadata.TenantId.Should().Be("test-tenant");
        });
    }

    [Fact]
    public async Task Rebuild_projections_replays_all_aggregates_batchwise_per_command()
    {
        await using var sp = TestHost.BuildInMemory();
        var store = sp.GetRequiredService<IEventStore<Order, string>>();
        var id = Guid.NewGuid().ToString("N");

        // Two commands => two batches during rebuild
        var order = Order.CreateNew(id);
        order.Create("ACME");
        await store.SaveAsync(order, TestHost.NewCommandContext());
        order.AddItem("Widget", 1m);
        await store.SaveAsync(order, TestHost.NewCommandContext());

        var projectionsBefore = TestDomain.EventRecorder.ProjectedAggregates.Count(x => x == id);

        await store.RebuildProjectionsAsync();

        TestDomain.EventRecorder.ProjectedAggregates.Count(x => x == id)
            .Should().Be(projectionsBefore + 2, "rebuild must trigger one projection update per command batch");
    }

    [Fact]
    public async Task Concurrent_save_with_stale_version_throws_concurrency_exception()
    {
        await using var sp = TestHost.BuildInMemory();
        var store = sp.GetRequiredService<IEventStore<Order, string>>();

        var order = Order.CreateNew("order-1");
        order.Create("ACME");
        await store.SaveAsync(order, TestHost.NewCommandContext());

        var copy1 = await store.LoadByIdAsync("order-1");
        var copy2 = await store.LoadByIdAsync("order-1");

        copy1!.AddItem("Widget", 1m);
        await store.SaveAsync(copy1, TestHost.NewCommandContext());

        copy2!.AddItem("Gadget", 2m);
        var act = () => store.SaveAsync(copy2, TestHost.NewCommandContext());

        await act.Should().ThrowAsync<ConcurrencyException<string>>();
    }
}
