using FEB.EventSourcing.Tests.Infrastructure;
using FEB.EventSourcing.Tests.TestDomain;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace FEB.EventSourcing.Tests.Core;

public class DispatchAndProjectionTests
{
    [Fact]
    public async Task Save_dispatches_events_to_scanned_sync_handlers()
    {
        await using var sp = TestHost.BuildInMemory();
        var store = sp.GetRequiredService<IEventStore<Order, string>>();
        var customer = $"sync-{Guid.NewGuid():N}";

        var order = Order.CreateNew(Guid.NewGuid().ToString("N"));
        order.Create(customer);
        await store.SaveAsync(order, TestHost.NewCommandContext());

        EventRecorder.SyncHandled.Should().Contain(customer,
            "ISyncEventHandlers registered by the assembly scan must be invoked on save");
    }

    [Fact]
    public async Task Save_updates_scanned_aggregate_projections()
    {
        await using var sp = TestHost.BuildInMemory();
        var store = sp.GetRequiredService<IEventStore<Order, string>>();
        var id = Guid.NewGuid().ToString("N");

        var order = Order.CreateNew(id);
        order.Create("ACME");
        await store.SaveAsync(order, TestHost.NewCommandContext());

        EventRecorder.ProjectedAggregates.Should().Contain(id,
            "IAggregateProjectionWriters registered by the assembly scan must be invoked on save");
    }

    [Fact]
    public void Scan_registers_handlers_under_their_generic_interfaces()
    {
        using var sp = TestHost.BuildInMemory();

        sp.GetServices<ISyncEventHandler<OrderCreated>>().Should().ContainSingle();
        sp.GetServices<IASyncEventHandler<OrderCreated>>().Should().ContainSingle();
        sp.GetServices<IAggregateProjectionWriter<Order>>().Should().ContainSingle();
    }
}
