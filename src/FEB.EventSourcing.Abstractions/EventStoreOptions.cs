namespace FEB.EventSourcing;

public sealed class EventStoreOptions
{
    public int SnapshotsEveryNEvents { get; private set; } = 10;

    public void SetSnapshotsEveryNEvents(int events)
    {
        SnapshotsEveryNEvents = events;
    }
}