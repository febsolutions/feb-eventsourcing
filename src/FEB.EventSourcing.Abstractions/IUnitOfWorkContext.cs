namespace FEB.EventSourcing;

/// <summary>
/// A transaction owned by the caller that the event store is currently taking part in
/// (see decision 0016). Store layers with effects outside the database — the Redis
/// cache, background snapshot writes — use it to defer those effects until the
/// caller has committed. Provider-neutral: relational providers implement it; where
/// nothing is registered, <see cref="NoUnitOfWorkContext"/> applies.
/// </summary>
public interface IUnitOfWorkContext
{
    /// <summary>True while a caller-owned transaction is active in this scope.</summary>
    bool IsActive { get; }

    /// <summary>
    /// Runs <paramref name="action"/> after the unit of work has committed; discards it
    /// on rollback. Without an active unit of work the action runs immediately.
    /// </summary>
    Task AfterCommitAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default);
}

/// <summary>The absence of a unit of work: never active, actions run immediately.</summary>
public sealed class NoUnitOfWorkContext : IUnitOfWorkContext
{
    public static NoUnitOfWorkContext Instance { get; } = new();

    private NoUnitOfWorkContext() { }

    public bool IsActive => false;

    public Task AfterCommitAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default)
        => action(cancellationToken);
}
