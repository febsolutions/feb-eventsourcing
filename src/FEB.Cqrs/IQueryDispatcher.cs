namespace FEB.Cqrs;

public interface IQueryDispatcher
{
    Task<TResult> ExecuteAsync<TResult>(IQuery<TResult> query, CancellationToken cancellationToken = default);
    
    Task<TResult> ExecuteAsync<TQuery, TResult>(TQuery query, CancellationToken cancellationToken = default)
        where TQuery : IQuery<TResult>;
}