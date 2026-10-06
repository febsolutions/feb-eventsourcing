namespace FEB.Cqrs;

public interface ICommandDispatcher
{
    Task ExecuteAsync(ICommand command, CancellationToken cancellationToken = default);
    
    Task ExecuteAsync<TCommand>(TCommand command, CancellationToken cancellationToken = default) where TCommand : ICommand;
    
    Task<TResult> ExecuteAsync<TResult>(ICommand<TResult> command, CancellationToken cancellationToken = default);
    
    Task<TResult> ExecuteAsync<TCommand, TResult>(TCommand command, CancellationToken cancellationToken = default) where TCommand : ICommand<TResult>;
}