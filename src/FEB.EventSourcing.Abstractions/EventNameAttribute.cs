namespace FEB.EventSourcing;

/// <summary>
/// Gives an event a stable name for storage, decoupled from its CLR type. Without
/// it the CLR type name is stored, which ties the data to the namespace, assembly
/// and class name — moving or renaming the event class then breaks reading old
/// events. With it, the class can move freely as long as the name stays the same.
/// <code>
/// [EventName("order.created")]
/// public sealed record OrderCreated(string Customer) : IDomainEvent&lt;Order&gt;;
/// </code>
/// The attribute is optional: events without it keep the previous behaviour, so
/// existing applications need no change. Use <see cref="Aliases"/> to keep reading
/// events that were already stored under a different name (e.g. when introducing
/// the attribute after a class has already moved).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class EventNameAttribute(string name) : Attribute
{
    /// <summary>
    /// The name written to the store. Pick something that is not a CLR name — a
    /// short, stable identifier such as <c>order.created</c> — and never change it
    /// afterwards; it is part of your data.
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    /// Former names this event is also read under (never written). The CLR type's
    /// short name stays readable automatically, so aliases are only needed for
    /// names that no longer match the current class — e.g. the old
    /// assembly-qualified name after a project move, or a previous
    /// <see cref="Name"/>.
    /// </summary>
    public string[] Aliases { get; init; } = [];
}
