using System.Data;
using System.Data.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FEB.EventSourcing.Sql;

/// <summary>
/// Scoped default implementation of <see cref="ISqlUnitOfWork"/>. Consecutive units of work
/// in one scope are allowed; a unit still active when the scope ends is discarded.
/// </summary>
public sealed class SqlUnitOfWork(
    ISqlDialect dialect,
    SqlEventStoreOptions options,
    ILogger<SqlUnitOfWork>? logger = null) : ISqlUnitOfWork, IDisposable
{
    /// <summary>
    /// A unit of work that is never active and cannot be joined — used by persistences that
    /// were constructed without one.
    /// </summary>
    internal static ISqlUnitOfWork Inactive { get; } = new InactiveUnitOfWork();

    private readonly ILogger _logger = logger ?? NullLogger<SqlUnitOfWork>.Instance;
    private readonly List<Func<CancellationToken, Task>> _afterCommit = [];
    private DbConnection? _connection;
    private DbTransaction? _transaction;

    public bool IsActive => _transaction != null;

    public DbConnection Connection
        => _connection ?? throw new InvalidOperationException("No unit of work is active.");

    public DbTransaction Transaction
        => _transaction ?? throw new InvalidOperationException("No unit of work is active.");

    public void Join(DbConnection connection, DbTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        if (IsActive)
            throw new InvalidOperationException("A unit of work is already active in this scope; complete or discard it first (concurrent units of work are not supported).");

        if (!ReferenceEquals(transaction.Connection, connection))
            throw new ArgumentException("The transaction does not belong to the given connection.", nameof(transaction));

        if (connection.State != ConnectionState.Open)
            throw new ArgumentException("The connection must be open.", nameof(connection));

        // Events must never land silently in another database than the configured store.
        string expectedDatabase;
        using (var configured = dialect.CreateConnection(options.ConnectionString))
            expectedDatabase = configured.Database;

        if (!string.Equals(connection.Database, expectedDatabase, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"The connection targets database '{connection.Database}', but the event store is configured for '{expectedDatabase}'.",
                nameof(connection));

        _connection = connection;
        _transaction = transaction;
        _afterCommit.Clear();
    }

    public Task AfterCommitAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (!IsActive)
            return action(cancellationToken);

        _afterCommit.Add(action);
        return Task.CompletedTask;
    }

    /// <summary>Never throws: after the commit, deferred actions are best-effort post-commit work.</summary>
    public async Task CompletedAsync(CancellationToken cancellationToken = default)
    {
        if (!IsActive)
        {
            _logger.LogWarning("CompletedAsync was called without an active unit of work");
            return;
        }

        var actions = _afterCommit.ToArray();
        End();

        // The data is committed: deferred effects are best effort and must not make the
        // caller believe the unit of work failed.
        foreach (var action in actions)
        {
            try
            {
                await action(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Deferred post-commit action failed");
            }
        }
    }

    public void Discarded() => End();

    /// <summary>
    /// End of the scope. A unit of work that is still active was never completed: whether
    /// the caller committed is unknown, so the deferred actions are dropped.
    /// </summary>
    public void Dispose()
    {
        if (!IsActive)
            return;

        _logger.LogWarning(
            "The scope ended while a unit of work was still active; it is discarded and {Count} deferred action(s) are dropped. " +
            "Call CompletedAsync after the commit or Discarded after a rollback.", _afterCommit.Count);
        End();
    }

    private void End()
    {
        _connection = null;
        _transaction = null;
        _afterCommit.Clear();
    }

    private sealed class InactiveUnitOfWork : ISqlUnitOfWork
    {
        public bool IsActive => false;
        public DbConnection Connection => throw new InvalidOperationException("No unit of work is active.");
        public DbTransaction Transaction => throw new InvalidOperationException("No unit of work is active.");

        public void Join(DbConnection connection, DbTransaction transaction)
            => throw new InvalidOperationException("This persistence was constructed without a unit of work; resolve it from the container to use ISqlUnitOfWork.");

        public Task AfterCommitAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default)
            => action(cancellationToken);

        public Task CompletedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Discarded() { }
    }
}
