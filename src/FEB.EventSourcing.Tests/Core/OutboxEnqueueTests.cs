using AwesomeAssertions;
using FEB.EventSourcing.Tests.Infrastructure;
using FEB.EventSourcing.Tests.TestDomain;
using Microsoft.Extensions.DependencyInjection;

namespace FEB.EventSourcing.Tests.Core;

/// <summary>
/// The enqueue path for outboxes that cannot be written atomically with the events
/// (custom outboxes, stores without a common transaction): every envelope of a save is
/// enqueued right after the append and before the projections.
/// </summary>
public class OutboxEnqueueTests
{
    internal sealed class RecordingOutboxPersistence : IOutboxPersistence
    {
        public List<OutboxEnvelope> Enqueued { get; } = [];

        public Task EnqueueAsync(OutboxEnvelope envelope, CancellationToken cancellationToken = default)
        {
            Enqueued.Add(envelope);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PendingOutboxEnvelope>> DequeueBatchAsync(IReadOnlyCollection<string> subscriberNames, int maxCount, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<PendingOutboxEnvelope>>([]);

        public Task MarkDispatchedAsync(OutboxEnvelope envelope, string subscriberName, IReadOnlyCollection<string> allSubscriberNames, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task MarkFailedAsync(OutboxEnvelope envelope, string subscriberName, Exception exception, IReadOnlyCollection<string> allSubscriberNames, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class RecordingOutbox : IOutbox
    {
        public List<OutboxEnvelope> Enqueued { get; } = [];

        public Task EnqueueAsync<TAggregate, TId>(OutboxEnvelope envelope, CancellationToken cancellationToken)
            where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new()
        {
            Enqueued.Add(envelope);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Persistence_without_batch_support_gets_every_envelope_of_a_save()
    {
        // A custom outbox persistence that does not override EnqueueManyAsync uses the
        // interface default, which enqueues the envelopes one by one.
        var persistence = new RecordingOutboxPersistence();
        await using var sp = TestHost.BuildInMemory(es => es.Services.AddSingleton<IOutbox>(new Outbox(persistence)));
        var store = sp.GetRequiredService<IEventStore<Order, string>>();

        var order = Order.CreateNew(Guid.NewGuid().ToString("N"));
        order.Create("ACME");
        order.AddItem("Widget", 1m);
        await store.SaveAsync(order, TestHost.NewCommandContext());

        persistence.Enqueued.Select(e => e.Metadata.Version).Should().Equal(0, 1);
    }

    [Fact]
    public async Task Custom_outbox_still_gets_envelopes_when_a_projection_fails()
    {
        var outbox = new RecordingOutbox();
        await using var sp = TestHost.BuildInMemory(es => es.Services.AddSingleton<IOutbox>(outbox));
        var store = sp.GetRequiredService<IEventStore<Order, string>>();

        var order = Order.CreateNew(Guid.NewGuid().ToString("N"));
        order.Create(FailingOrderProjectionWriter.FailMarker + "custom");

        var save = () => store.SaveAsync(order, TestHost.NewCommandContext());
        await save.Should().ThrowAsync<InvalidOperationException>();

        outbox.Enqueued.Should().ContainSingle("the outbox is written before the projections run");
    }
}
