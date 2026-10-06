namespace FEB.EventSourcing;

public interface ISyncEventHandler;

/// <summary>
/// Handle Events synchronously via EventDispatcher
/// </summary>
/// <typeparam name="TEvent"></typeparam>
public interface ISyncEventHandler<in TEvent> : ISyncEventHandler
{
    Task HandleAsync(TEvent @event, EventHandlingContext context, CancellationToken cancellationToken);
}