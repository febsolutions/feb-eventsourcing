using Microsoft.Extensions.DependencyInjection;

namespace FEB.Cqrs;

internal sealed class QueryDispatcher(IServiceProvider serviceProvider) : IQueryDispatcher
{
    public Task<TResult> ExecuteAsync<TQuery, TResult>(TQuery query, CancellationToken cancellationToken = default)
        where TQuery : IQuery<TResult>
    {
        var handler = serviceProvider.GetRequiredService<IQueryHandler<TQuery, TResult>>();
        return handler.HandleAsync(query, cancellationToken);
    }
    
    public Task<TResult> ExecuteAsync<TResult>(IQuery<TResult> query, CancellationToken cancellationToken = default)
    {
        var handlerType = typeof(IQueryHandler<,>)
            .MakeGenericType(query.GetType(), typeof(TResult));

        dynamic handler = serviceProvider.GetRequiredService(handlerType);
        return handler.HandleAsync((dynamic)query, cancellationToken);
    }
}

