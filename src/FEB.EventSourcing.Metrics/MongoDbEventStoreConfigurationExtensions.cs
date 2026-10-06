using FEB.EventSourcing.Metrics;
using Microsoft.Extensions.DependencyInjection;

// ReSharper disable once CheckNamespace
namespace FEB.EventSourcing;

public static class MetricsEventStoreConfigurationExtensions
{
    public static IEventSourcingBuilder UsePrometheusMetrics(
        this IEventSourcingBuilder builder)
    {
        builder.RegisterInfrastructure(services =>
        {
            services.AddSingleton<IEventStoreMetrics, PrometheusEventStoreMetrics>();
        });

        return builder;
    }
}
