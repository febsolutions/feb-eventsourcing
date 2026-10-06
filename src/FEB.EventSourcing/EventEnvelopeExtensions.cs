using System.Text.Json;

namespace FEB.EventSourcing;

public static class EventEnvelopeExtensions
{
    public static OutboxEnvelope ToOutboxEnvelope<TAggregate, TId>(this EventEnvelope<TAggregate, TId> envelope)
        where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new()
    {
        return new OutboxEnvelope(
            Payload: new OutboxPayload(
                // [EventName] if set, otherwise the CLR name as before
                EventType: EventTypeNames.GetConfiguredName(envelope.Payload.GetType())
                           ?? envelope.Payload.GetType().AssemblyQualifiedName!,
                // Pass the runtime type: with the declared interface type STJ would write "{}"
                Data: JsonSerializer.Serialize(envelope.Payload, envelope.Payload.GetType())
            ),
            Metadata: new OutboxMetadata(
                EventId: envelope.Metadata.EventId,
                AggregateType: envelope.Metadata.AggregateType,
                AggregateId: envelope.Metadata.AggregateId!.ToString()!,
                Version: envelope.Metadata.Version,
                OccurredAt: envelope.Metadata.OccurredAt,
                TenantId: envelope.Metadata.TenantId,
                UserId: envelope.Metadata.UserId,
                CorrelationId: envelope.Metadata.CorrelationId,
                CausationId: envelope.Metadata.CausationId,
                Headers: envelope.Metadata.Headers
            )
        );
    }

}