using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FEB.EventSourcing;

public static class OutboxConfigurationExtensions
{
    /// <summary>
    /// Hosts the default <see cref="OutboxWorker"/>. Requires an outbox persistence
    /// (e.g. <c>UseMongoOutbox()</c>). If no <see cref="IOutboxSubscriber"/> is registered,
    /// the default subscriber (<see cref="DefaultOutboxDispatcher"/>, name "handlers",
    /// delivers to <c>IASyncEventHandler&lt;T&gt;</c>) is added.
    /// </summary>
    public static IEventSourcingBuilder UseOutboxWorker(
        this IEventSourcingBuilder builder,
        Action<OutboxWorkerOptions>? configure = null)
    {
        builder.RegisterInfrastructure(services =>
        {
            var options = new OutboxWorkerOptions();
            configure?.Invoke(options);

            services.AddSingleton(options);

            if (services.All(d => d.ServiceType != typeof(IOutboxSubscriber)))
                services.AddScoped<IOutboxSubscriber, DefaultOutboxDispatcher>();

            services.AddHostedService<OutboxWorker>();
        });

        return builder;
    }

    /// <summary>
    /// Adds a named outbox subscriber. Call multiple times for multiple consumers —
    /// each is tracked, retried and dead-lettered independently. Typical use: a message
    /// broker publisher (which then fans out itself) or a third-party API client.
    /// </summary>
    public static IEventSourcingBuilder UseOutboxSubscriber<TSubscriber>(this IEventSourcingBuilder builder)
        where TSubscriber : class, IOutboxSubscriber
    {
        builder.RegisterInfrastructure(services =>
        {
            services.AddScoped<IOutboxSubscriber, TSubscriber>();
        });

        return builder;
    }

    /// <summary>
    /// Keeps the in-process handler delivery (<see cref="DefaultOutboxDispatcher"/>) in
    /// addition to other subscribers. Only needed when you add custom subscribers
    /// but still want <c>IASyncEventHandler&lt;T&gt;</c> delivery.
    /// </summary>
    public static IEventSourcingBuilder UseDefaultOutboxSubscriber(this IEventSourcingBuilder builder)
        => builder.UseOutboxSubscriber<DefaultOutboxDispatcher>();

    /// <summary>
    /// Backwards-compatible: registers a single-consumer <see cref="IOutboxDispatcher"/>
    /// as the only subscriber (name = type name), replacing the default one.
    /// Prefer <see cref="UseOutboxSubscriber{TSubscriber}"/> for new code.
    /// </summary>
    public static IEventSourcingBuilder UseOutboxDispatcher<TDispatcher>(this IEventSourcingBuilder builder)
        where TDispatcher : class, IOutboxDispatcher
    {
        builder.RegisterInfrastructure(services =>
        {
            services.RemoveAll<IOutboxDispatcher>();
            services.RemoveAll<IOutboxSubscriber>();
            services.AddScoped<IOutboxDispatcher, TDispatcher>();
            services.AddScoped<IOutboxSubscriber>(sp =>
                new DispatcherSubscriberAdapter(typeof(TDispatcher).Name, sp.GetRequiredService<IOutboxDispatcher>()));
        });

        return builder;
    }

    private sealed class DispatcherSubscriberAdapter(string name, IOutboxDispatcher dispatcher) : IOutboxSubscriber
    {
        public string Name => name;

        public Task DispatchAsync(OutboxEnvelope envelope, CancellationToken cancellationToken = default)
            => dispatcher.DispatchAsync(envelope, cancellationToken);
    }
}
