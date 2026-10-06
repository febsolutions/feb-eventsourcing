using Microsoft.Extensions.DependencyInjection;

namespace FEB.Cqrs;

internal class CommandDispatcher(IServiceProvider serviceProvider) : ICommandDispatcher
{
    public Task<TResult> ExecuteAsync<TCommand, TResult>(TCommand command, CancellationToken cancellationToken = default)
        where TCommand : ICommand<TResult>
    {
        var handler = serviceProvider.GetRequiredService<ICommandHandler<TCommand, TResult>>();
        return handler.HandleAsync(command,cancellationToken);
    }

    public Task<TResult> ExecuteAsync<TResult>(ICommand<TResult> command, CancellationToken cancellationToken = default)
    {
        var handlerType = typeof(ICommandHandler<,>)
            .MakeGenericType(command.GetType(), typeof(TResult));

        dynamic handler = serviceProvider.GetRequiredService(handlerType);
        return handler.HandleAsync((dynamic)command, cancellationToken);
    }
    
    public Task ExecuteAsync<TCommand>(TCommand command, CancellationToken cancellationToken = default)
        where TCommand : ICommand
    {
        var handler = serviceProvider.GetRequiredService<ICommandHandler<TCommand>>();
        return handler.HandleAsync(command, cancellationToken);
    }
    
    public Task ExecuteAsync(ICommand command, CancellationToken cancellationToken = default)
    {
        // Non-generic overload: resolve ICommandHandler<TCommand> for the runtime type.
        var handlerType = typeof(ICommandHandler<>)
            .MakeGenericType(command.GetType());

        dynamic handler = serviceProvider.GetRequiredService(handlerType);
        return handler.HandleAsync((dynamic)command, cancellationToken);
    }
}