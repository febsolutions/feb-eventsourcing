using System.Data.Common;

namespace FEB.EventSourcing.Sql;

/// <summary>
/// Lets the relational event store take part in a transaction the caller already owns —
/// typically the application's EF Core transaction — so that events, outbox envelopes,
/// projections and the application's own table writes commit or roll back together
/// (decision 0016). Registered as a scoped service by <c>UsePostgres</c> /
/// <c>UseSqlServer</c> and inactive until <see cref="Join"/> is called; without it the
/// store behaves exactly as before.
/// </summary>
public interface ISqlUnitOfWork : IUnitOfWorkContext
{
    /// <summary>The caller's connection. Throws if no unit of work is active.</summary>
    DbConnection Connection { get; }

    /// <summary>The caller's transaction. Throws if no unit of work is active.</summary>
    DbTransaction Transaction { get; }

    /// <summary>
    /// Joins a transaction the caller owns. After <see cref="CompletedAsync"/> or
    /// <see cref="Discarded"/> the scope may join again (one unit of work per aggregate in a
    /// loop); two units of work at the same time are not supported. The event store then reads and writes through
    /// it and never commits, rolls back or disposes it. Throws if the transaction does not
    /// belong to the open <paramref name="connection"/>, if the connection targets another
    /// database than the store's, or if a unit of work is already active.
    /// </summary>
    void Join(DbConnection connection, DbTransaction transaction);

    /// <summary>
    /// Called by the caller after its transaction has committed — outside of the code that
    /// would roll back on failure: ends the unit of work and runs the deferred
    /// <see cref="IUnitOfWorkContext.AfterCommitAsync"/> actions. Never throws; a failing
    /// action is logged and the next one runs.
    /// </summary>
    Task CompletedAsync(CancellationToken cancellationToken = default);

    /// <summary>Called by the caller after a rollback: ends the unit of work and drops the deferred actions.</summary>
    void Discarded();
}
