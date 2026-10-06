namespace FEB.EventSourcing;

internal static class CommandContextExtensions
{
    internal static ProjectionContext ToProjectionContext(this CommandContext context)
    {
        return new ProjectionContext(
            context.TenantId,
            context.UserId,
            context.CorrelationId,
            context.CommandId.ToString(),
            context.Headers,
            context.ReceivedAt);
    }
}