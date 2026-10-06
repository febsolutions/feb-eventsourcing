namespace FEB.EventSourcing;

public sealed record ProjectionContext(
    string? TenantId, 
    string? UserId,
    string CorrelationId, 
    string CommandId, 
    IReadOnlyDictionary<string, string>? Headers, 
    DateTime ReceivedAt);