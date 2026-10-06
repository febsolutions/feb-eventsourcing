namespace FEB.EventSourcing;

public interface IASyncEventHandler;

/// <summary>
/// Handle events asynchronously via Outbox
/// </summary>
/// <typeparam name="TEvent"></typeparam>
public interface IASyncEventHandler<in TEvent> : IASyncEventHandler
{
    Task HandleAsync(TEvent @event, EventHandlingContext context, CancellationToken cancellationToken);
}