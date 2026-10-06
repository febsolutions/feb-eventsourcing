using FEB.EventSourcing.InMemory;
using FEB.EventSourcing.Tests.TestDomain;
using Microsoft.Extensions.DependencyInjection;

namespace FEB.EventSourcing.Tests.Infrastructure;

public static class TestHost
{
    /// <summary>
    /// Builds a service provider with in-memory persistence and the test aggregates.
    /// </summary>
    public static ServiceProvider BuildInMemory(Action<IEventSourcingBuilder>? configureExtra = null)
    {
        var services = new ServiceCollection();

        services.AddEventSourcing(es =>
        {
            es.UseInMemory();

            configureExtra?.Invoke(es);

            es.Aggregate<Order, string>(a => a.Factory(Order.CreateNew));
            es.Aggregate<Customer, string>(a => a.Factory(Customer.CreateNew));
        }, typeof(TestHost).Assembly);

        return services.BuildServiceProvider();
    }

    public static CommandContext NewCommandContext() => new()
    {
        CommandId = Guid.NewGuid(),
        CorrelationId = Guid.NewGuid().ToString("N"),
        TenantId = "test-tenant",
        UserId = "test-user",
        ReceivedAt = DateTime.UtcNow
    };

    /// <summary>
    /// Event envelope for tests directly on the persistence level (where the
    /// CoreEventStore would otherwise set the metadata). Version must match ExpectedVersion.
    /// </summary>
    public static EventEnvelope<Order, string> NewEnvelope(string aggregateId, int version, IDomainEvent<Order> payload, Guid? eventId = null)
        => new(payload, new EventMetadata<string>(
            EventId: eventId ?? Guid.NewGuid(),
            AggregateId: aggregateId,
            AggregateType: nameof(Order),
            Version: version,
            OccurredAt: DateTime.UtcNow,
            TenantId: "test-tenant",
            UserId: "test-user",
            CorrelationId: Guid.NewGuid().ToString("N"),
            CausationId: Guid.NewGuid().ToString(),
            Headers: null));
}
