namespace FEB.EventSourcing.MongoDb;

public class EventStoreCollectionNameBuilder(string? eventStorePrefix)
{
    public string GetEventStoreName<T>() => CombineWithPrefix($"{typeof(T).Name}Events");

    public string GetSnapshotName<T>() => CombineWithPrefix($"{typeof(T).Name}Snapshots");
    
    public string GetSnapshotName(Type type) => CombineWithPrefix($"{type.Name}Snapshots");

    public string GetAggregateVersionsStoreName<T>() => CombineWithPrefix($"{typeof(T).Name}Versions");
    

    private string CombineWithPrefix(string name)
    {
        return string.IsNullOrEmpty(eventStorePrefix) ? name : $"{eventStorePrefix}.{name}";
    }
}