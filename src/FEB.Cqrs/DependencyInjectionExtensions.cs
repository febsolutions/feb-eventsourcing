using Microsoft.Extensions.DependencyInjection;

namespace FEB.Cqrs;

public static class DependencyInjectionExtensions
{
    public static void AddCqrs(this IServiceCollection services)
    {
        services.AddScoped<ICommandDispatcher, CommandDispatcher>();
        services.AddScoped<IQueryDispatcher, QueryDispatcher>();
    }
}