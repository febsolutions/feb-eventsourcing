using FEB.EventSourcing.Tests.Infrastructure;
using FEB.EventSourcing.Tests.TestDomain;
using AwesomeAssertions;

namespace FEB.EventSourcing.Tests.Core;

/// <summary>
/// Stable storage names for events ([EventName]): resolution, aliases, uniqueness.
/// The attribute is optional - events without it keep the CLR name.
/// </summary>
public class EventNameTests
{
    [Fact]
    public void Configured_name_comes_from_the_attribute()
    {
        EventTypeNames.GetConfiguredName(typeof(OrderRenamed)).Should().Be("order.renamed");
    }

    [Fact]
    public void Events_without_the_attribute_have_no_configured_name()
    {
        EventTypeNames.GetConfiguredName(typeof(OrderCreated)).Should()
            .BeNull("without the attribute every persistence must keep its previous CLR default");
    }

    [Fact]
    public void Stored_name_resolves_back_to_the_type()
    {
        // Registration happens in AddEventSourcing; building a host is enough.
        using var _ = TestHost.BuildInMemory();

        EventTypeNames.Resolve("order.renamed").Should().Be(typeof(OrderRenamed));
    }

    [Fact]
    public void Aliases_resolve_to_the_same_type()
    {
        using var _ = TestHost.BuildInMemory();

        EventTypeNames.Resolve("FEB.EventSourcing.Tests.TestDomain.Legacy.OrderRenamedV1, FEB.EventSourcing.Tests")
            .Should().Be(typeof(OrderRenamed), "data of moved events must stay readable");
    }

    [Fact]
    public void Unknown_name_falls_back_to_the_clr_type_name()
    {
        // This is what every persistence wrote before the attribute existed.
        EventTypeNames.ResolveRequired(typeof(OrderCreated).AssemblyQualifiedName!)
            .Should().Be(typeof(OrderCreated));
    }

    [Fact]
    public void Unresolvable_name_names_the_remedy()
    {
        var act = () => EventTypeNames.ResolveRequired("Shop.Catalog.Domain.ArticleCreatedV1, Shop.Catalog.Domain");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Shop.Catalog.Domain.ArticleCreatedV1*")
            .WithMessage("*EventName*", "the message must name the remedy, not just the type");
    }

    [Fact]
    public void Two_events_claiming_the_same_name_are_rejected()
    {
        EventTypeNames.Register(typeof(FirstClaim));

        var act = () => EventTypeNames.Register(typeof(SecondClaim));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*duplicate.name*")
            .WithMessage("*FirstClaim*").WithMessage("*SecondClaim*");
    }

    [Fact]
    public void Empty_name_is_rejected()
    {
        var act = () => EventTypeNames.GetConfiguredName(typeof(BlankName));

        act.Should().Throw<InvalidOperationException>().WithMessage("*empty name*");
    }

    // Deliberately not IEvent: these types must only appear through explicit registration,
    // not in the assembly scan of every test host.
    [EventName("duplicate.name")]
    private sealed record FirstClaim;

    [EventName("duplicate.name")]
    private sealed record SecondClaim;

    [EventName("   ")]
    private sealed record BlankName;
}
