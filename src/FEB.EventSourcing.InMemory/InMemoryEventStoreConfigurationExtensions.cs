using Microsoft.Extensions.DependencyInjection;

namespace FEB.EventSourcing.InMemory;

public static class InMemoryEventStoreConfigurationExtensions
{
    public static void UseInMemory(
        this IEventSourcingBuilder builder)
    {
        if (builder.IsPersistenceRegistered)
            throw new InvalidOperationException("Persistence already registered.");

        builder.RegisterInfrastructure(services =>
        {

            builder.Services.AddSingleton<IEventStorePersistence, InMemoryEventStorePersistence>();

            builder.SetPersistenceRegistered();
        });
    }
}