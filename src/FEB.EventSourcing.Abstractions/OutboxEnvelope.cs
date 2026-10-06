namespace FEB.EventSourcing;

public sealed record OutboxEnvelope(
    OutboxPayload Payload,
    OutboxMetadata Metadata
);