namespace FEB.EventSourcing;

public interface IEventStoreMetrics
{
    void RecordRead<T>(string source, double durationSeconds);
    
    void RecordRead(Type type, string source, double durationSeconds);
    
    void RecordWrite<T>(string source, double durationSeconds);
    
    void RecordWrite(Type type, string source, double durationSeconds);
    
    void IncrementRead<T>(string source);

    void IncrementRead(Type type, string source);

    void IncrementWrite<T>(string source);
    
    void IncrementWrite(Type type, string source);
}