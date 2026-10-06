namespace FEB.EventSourcing;

public sealed record OutboxPayload(
    string EventType,
    string Data
);