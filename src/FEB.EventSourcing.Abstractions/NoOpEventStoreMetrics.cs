namespace FEB.EventSourcing;

public class NoOpEventStoreMetrics : IEventStoreMetrics
{
    public void RecordRead<T>(string source, double durationSeconds) { }
    public void RecordRead(Type type, string source, double durationSeconds) {}

    public void RecordWrite<T>(string source, double durationSeconds) { }
    public void RecordWrite(Type type, string source, double durationSeconds) {}

    public void IncrementRead<T>(string source) { }
    public void IncrementRead(Type type, string source) {}

    public void IncrementWrite<T>(string source) { }
    public void IncrementWrite(Type type, string source) {}
}