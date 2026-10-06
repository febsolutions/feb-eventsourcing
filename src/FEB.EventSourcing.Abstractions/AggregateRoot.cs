namespace FEB.EventSourcing;

public abstract class AggregateRoot
{
    public abstract void EnsureHasId();
}

public abstract class AggregateRoot<TAggregate> : AggregateRoot
    where TAggregate : AggregateRoot<TAggregate>, IEntity
{
    private readonly List<IDomainEvent<TAggregate>> _uncommittedEvents = new();
    public int Version { get; protected set; } = -1;
    
    public IReadOnlyCollection<IDomainEvent<TAggregate>> GetUncommittedEvents()
        => _uncommittedEvents.AsReadOnly();
    
    private void ClearUncommittedEvents()
        => _uncommittedEvents.Clear();
    
    protected void Raise(IDomainEvent<TAggregate> @event)
    {
        @event.ApplyTo((TAggregate)this);
        _uncommittedEvents.Add(@event);
    }
    
    protected internal void Replay(IDomainEvent<TAggregate> @event)
    {
        @event.ApplyTo((TAggregate)this);
        Version++;
    }
    
    protected void RestoreState(int version)
    {
        Version = version;
    }
    
    public void Commit(int newVersion)
    {
        if (newVersion < Version)
            throw new InvalidOperationException($"Commit version {newVersion} < current {Version}.");

        ClearUncommittedEvents();
        Version = newVersion;
    }
    
    protected internal virtual void OnAfterRehydrate()
    {
    }
}

public abstract class AggregateRoot<TAggregate, TId> : AggregateRoot<TAggregate>, IEntity<TId> 
    where TAggregate : AggregateRoot<TAggregate>, IEntity<TId>, new()
{
    public TId Id { get; set; }
   
    public static TAggregate CreateNew(TId id) 
    {
        var entity = new TAggregate { Id = id };
        entity.EnsureHasId();

        return entity;
    }
        
    public void Replay(IEnumerable<IDomainEvent<TAggregate>> events)
    {
        EnsureHasId();

        foreach (var e in events)
            Replay(e);

        OnAfterRehydrate();
    }

    public void RestoreState(int version, IEnumerable<IDomainEvent<TAggregate>> events)
    {
        EnsureHasId();
        
        RestoreState(version);

        foreach (var e in events)
            Replay(e);

        OnAfterRehydrate();
    }

}