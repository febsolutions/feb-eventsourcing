namespace FEB.EventSourcing;

public sealed class SnapshotBuilder
{
    private int _everyNEvents;

    public SnapshotBuilder EveryNEvents(int count)
    {
        _everyNEvents = count;
        return this;
    }

    internal SnapshotOptions Build()
        => new(_everyNEvents);
}