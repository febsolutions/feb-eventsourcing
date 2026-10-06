namespace FEB.EventSourcing;

public static class OutboxSubscriberName
{
    /// <summary>
    /// Subscriber names are used as persistence keys (e.g. MongoDB field paths), so
    /// they must be non-empty and free of '.', '$' and whitespace.
    /// </summary>
    public static void Validate(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Outbox subscriber name must not be empty.", nameof(name));

        if (name.Any(c => c == '.' || c == '$' || char.IsWhiteSpace(c)))
            throw new ArgumentException($"Outbox subscriber name '{name}' must not contain '.', '$' or whitespace.", nameof(name));
    }
}
