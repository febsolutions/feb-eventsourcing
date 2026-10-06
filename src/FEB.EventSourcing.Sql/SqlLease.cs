using System.Data;
using System.Data.Common;

namespace FEB.EventSourcing.Sql;

/// <summary>
/// The connection (and transaction) one persistence operation works on: the caller's when
/// a unit of work is active — never committed, rolled back or disposed here — otherwise a
/// connection of its own, owned and disposed by the lease.
/// </summary>
internal sealed class SqlLease : IAsyncDisposable
{
    private readonly bool _owned;

    private SqlLease(DbConnection connection, DbTransaction? transaction, bool owned)
    {
        Connection = connection;
        Transaction = transaction;
        _owned = owned;
    }

    public DbConnection Connection { get; }
    public DbTransaction? Transaction { get; }

    public static async Task<SqlLease> OpenAsync(ISqlDialect dialect, SqlEventStoreOptions options,
        ISqlUnitOfWork unitOfWork, bool transactional, CancellationToken cancellationToken)
    {
        if (unitOfWork.IsActive)
            return new SqlLease(unitOfWork.Connection, unitOfWork.Transaction, owned: false);

        var connection = dialect.CreateConnection(options.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            var transaction = transactional
                ? await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                : null;
            return new SqlLease(connection, transaction, owned: true);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public DbCommand CreateCommand(string sql)
    {
        var command = Connection.CreateCommand();
        command.Transaction = Transaction;
        command.CommandText = sql;
        return command;
    }

    public Task<int> ExecuteAsync(string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
        => SqlEventStorePersistence.ExecuteAsync(Connection, Transaction, sql, cancellationToken, parameters);

    /// <summary>Commits an owned transaction; a joined one belongs to the caller.</summary>
    public Task CommitAsync(CancellationToken cancellationToken)
        => _owned && Transaction != null ? Transaction.CommitAsync(cancellationToken) : Task.CompletedTask;

    /// <summary>Rolls back an owned transaction; a joined one belongs to the caller.</summary>
    public Task RollbackAsync(CancellationToken cancellationToken)
        => _owned && Transaction != null ? Transaction.RollbackAsync(cancellationToken) : Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (!_owned)
            return;

        if (Transaction != null)
            await Transaction.DisposeAsync();
        await Connection.DisposeAsync();
    }
}
