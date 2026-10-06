namespace FEB.EventSourcing.InMemory;

internal sealed class StreamState
{
    public List<object> Events { get; } = new();
    public object SyncRoot { get; } = new();
}