using FEB.EventSourcing.Tests.TestDomain;
using AwesomeAssertions;

namespace FEB.EventSourcing.Tests.Core;

public class AggregateRootTests
{
    [Fact]
    public void CreateNew_sets_id_and_starts_at_version_minus_one()
    {
        var order = Order.CreateNew("order-1");

        order.Id.Should().Be("order-1");
        order.Version.Should().Be(-1);
        order.GetUncommittedEvents().Should().BeEmpty();
    }

    [Fact]
    public void Raise_applies_event_and_collects_it_as_uncommitted()
    {
        var order = Order.CreateNew("order-1");

        order.Create("ACME");
        order.AddItem("Widget", 9.99m);

        order.Customer.Should().Be("ACME");
        order.Items.Should().ContainSingle().Which.Should().Be("Widget");
        order.Total.Should().Be(9.99m);
        order.GetUncommittedEvents().Should().HaveCount(2);
        order.Version.Should().Be(-1, "Raise must not increase the version; that only happens on commit");
    }

    [Fact]
    public void Commit_clears_uncommitted_events_and_sets_version()
    {
        var order = Order.CreateNew("order-1");
        order.Create("ACME");
        order.AddItem("Widget", 1m);

        order.Commit(1);

        order.GetUncommittedEvents().Should().BeEmpty();
        order.Version.Should().Be(1);
    }

    [Fact]
    public void Commit_with_lower_version_throws()
    {
        var order = Order.CreateNew("order-1");
        order.Create("ACME");
        order.Commit(5);

        var act = () => order.Commit(3);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Replay_applies_events_and_increments_version_per_event()
    {
        var order = Order.CreateNew("order-1");

        order.Replay(new IDomainEvent<Order>[]
        {
            new OrderCreated("ACME"),
            new OrderItemAdded("Widget", 2m),
            new OrderItemAdded("Gadget", 3m)
        });

        order.Version.Should().Be(2, "three events from start version -1 result in version 2");
        order.Customer.Should().Be("ACME");
        order.Total.Should().Be(5m);
        order.GetUncommittedEvents().Should().BeEmpty("replay produces no uncommitted events");
    }

    [Fact]
    public void RestoreState_sets_base_version_and_replays_delta()
    {
        var order = Order.CreateNew("order-1");

        order.RestoreState(4, new IDomainEvent<Order>[]
        {
            new OrderItemAdded("Widget", 2m)
        });

        order.Version.Should().Be(5);
        order.Items.Should().ContainSingle();
    }
}
