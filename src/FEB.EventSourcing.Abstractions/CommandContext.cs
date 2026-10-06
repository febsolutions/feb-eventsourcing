namespace FEB.EventSourcing;

public sealed record CommandContext 
{
    // Identity and security
    public string? TenantId { get; init; }
    public string? UserId { get; init; }

    // Tracing
    public string CorrelationId { get; init; } = null!;
    public Guid CommandId { get; init; }

    // Optional technical information
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    // Optional: when the command was received (not the event)
    public DateTime ReceivedAt { get; init; }
}