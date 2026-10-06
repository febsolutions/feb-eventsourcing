using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace FEB.EventSourcing;

public static class ServiceCollectionExtensions
{
    public static void AddEventSourcing(
        this IServiceCollection services,
        Action<IEventSourcingBuilder> configure,
        params Assembly[] assemblies)
    {
        var builder = new EventSourcingBuilder(services, assemblies.Distinct().ToList());
        services.AddSingleton<IEventSourcingBuilder>(builder);

        // Make stored name -> type resolvable ([EventName]); type -> name works through
        // the attribute itself and needs no registration.
        EventTypeNames.RegisterAssemblies(builder.ApplicationAssemblies);
        
        builder.Services.AddSingleton<IEventStoreMetrics, NoOpEventStoreMetrics>();

        builder.Services.AddScoped<IProjectionUpdater, DefaultProjectionUpdater>();
        
        builder.Services.AddScoped<IEventDispatcher, DefaultEventDispatcher>();
       
        // Outbox and worker are opt-in: es.UseMongoOutbox()/UseSqlOutbox() and es.UseOutboxWorker().
        
        builder.Services.AddSingleton<IAggregateIdResolver, DefaultAggregateIdResolver>();
        
        builder.EventStoreChainBuilder.SetBase(new EventStoreCoreRegistration());
        
        RegisterFromAssemblies<IProjectionWriter>(services, assemblies);

        RegisterFromAssemblies<ISyncEventHandler>(services, assemblies);
        
        RegisterFromAssemblies<IASyncEventHandler>(services, assemblies);

        configure(builder);
    }
    
    private static void RegisterFromAssemblies<T>(
        IServiceCollection services,
        params Assembly[] additionalAssemblies)
    {
        var assemblies = new HashSet<Assembly>();

        foreach (var asm in additionalAssemblies)
            assemblies.Add(asm);

        foreach (var assembly in assemblies)
            RegisterFromAssembly<T>(services, assembly);
    }
    
    private static void RegisterFromAssembly<T>(
        IServiceCollection services,
        Assembly assembly)
    {
        var marker = typeof(T);

        var implTypes = assembly
            .GetTypes()
            .Where(t =>
                t is { IsAbstract: false, IsInterface: false } &&
                marker.IsAssignableFrom(t));

        foreach (var implType in implTypes)
        {
            // Only add if not already present (no duplicate registration)
            if (!services.Any(sd => sd.ServiceType == marker && sd.ImplementationType == implType))
            {
                services.AddScoped(marker, implType);
            }

            // Additionally register under the concrete generic interfaces
            // (ISyncEventHandler<TEvent>, IAggregateProjectionWriter<T>, ...) -
            // dispatchers and the projection updater resolve exactly those.
            foreach (var iface in implType.GetInterfaces().Where(i => i != marker && marker.IsAssignableFrom(i)))
            {
                if (!services.Any(sd => sd.ServiceType == iface && sd.ImplementationType == implType))
                {
                    services.AddScoped(iface, implType);
                }
            }
        }
    }
}