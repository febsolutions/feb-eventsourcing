using System.Data.Common;

namespace FEB.EventSourcing.Sql;

/// <summary>
/// The provider-specific part of the SQL persistence. Everything the core needs that
/// differs between databases: connections, DDL, upsert syntax, the outbox lease query
/// and duplicate-key detection. Implemented by FEB.EventSourcing.Postgres and
/// FEB.EventSourcing.SqlServer; a third dialect (e.g. MySQL 8) would slot in here.
/// </summary>
public interface ISqlDialect
{
    /// <summary>Human-readable name for logs/metrics (e.g. "postgres").</summary>
    string Name { get; }

    DbConnection CreateConnection(string connectionString);

    /// <summary>
    /// Idempotent DDL for events, versions, snapshots and outbox tables of the given
    /// schema/prefix — including the unique index on (aggregate_type, aggregate_id, version).
    /// Executed by <see cref="SqlSchemaInitializer"/> at startup.
    /// </summary>
    IReadOnlyList<string> GetSchemaStatements(SqlEventStoreOptions options);

    /// <summary>
    /// SQL that inserts a version row for a brand-new aggregate and returns 1 row if it
    /// was inserted, 0 rows if the id already existed (no exception). Parameters:
    /// @aggregate_type, @aggregate_id, @version.
    /// </summary>
    string InsertVersionIfAbsent(SqlEventStoreOptions options);

    /// <summary>
    /// SQL that upserts the snapshot row for an aggregate (one latest snapshot per id).
    /// Parameters: @aggregate_type, @aggregate_id, @stream_version, @schema_version,
    /// @payload, @compression, @created_utc.
    /// </summary>
    string UpsertSnapshot(SqlEventStoreOptions options);

    /// <summary>
    /// SQL that atomically leases up to @max_count outbox rows (not completed, not
    /// currently leased) oldest-first, sets locked_until = @locked_until and returns the
    /// leased rows (id, payload_json, metadata_json, created_at, deliveries_json).
    /// Postgres/SQL Server implement this with SKIP LOCKED / READPAST.
    /// </summary>
    string LeaseOutboxBatch(SqlEventStoreOptions options);

    /// <summary>True if the exception is a unique-constraint / duplicate-key violation.</summary>
    bool IsDuplicateKey(DbException exception);

    /// <summary>
    /// True if the exception reports a conflict with a concurrent writer that is not a
    /// duplicate key — serialization failure, snapshot update conflict, deadlock victim.
    /// Such errors are surfaced as <c>ConcurrencyException</c> so the caller's retry logic
    /// applies; they occur mainly under stricter isolation levels set by a caller-owned
    /// transaction. The default recognises none.
    /// </summary>
    bool IsConcurrencyConflict(DbException exception) => false;

    /// <summary>
    /// Wraps a text parameter placeholder for a JSON column, e.g. Postgres needs
    /// <c>@p::jsonb</c> while SQL Server stores JSON as nvarchar and returns the
    /// placeholder unchanged.
    /// </summary>
    string JsonParameter(string placeholder);

    /// <summary>Reads a JSON column as text in SELECTs (Postgres: <c>col::text</c>).</summary>
    string JsonToText(string column);

    /// <summary>
    /// Bounded delete of completed outbox envelopes older than @cutoff, at most @batch
    /// rows per call (parameters: @cutoff, @batch). Used by the retention cleanup.
    /// </summary>
    string DeleteCompletedOutbox(SqlEventStoreOptions options);
}
