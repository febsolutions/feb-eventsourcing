using System.Data.Common;
using FEB.EventSourcing.Sql;
using Npgsql;

namespace FEB.EventSourcing.Postgres;

public sealed class PostgresDialect : ISqlDialect
{
    public string Name => "postgres";

    public DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

    public IReadOnlyList<string> GetSchemaStatements(SqlEventStoreOptions o) =>
    [
        $"CREATE SCHEMA IF NOT EXISTS {o.Schema}",

        $"""
        CREATE TABLE IF NOT EXISTS {o.Qualified(o.EventsTable)} (
            event_id        uuid PRIMARY KEY,
            aggregate_type  varchar(200) NOT NULL,
            aggregate_id    varchar(200) NOT NULL,
            version         integer NOT NULL,
            event_type      varchar(1000) NOT NULL,
            payload         jsonb NOT NULL,
            occurred_at     timestamptz NOT NULL,
            tenant_id       varchar(200),
            user_id         varchar(200),
            correlation_id  varchar(200) NOT NULL,
            causation_id    varchar(200),
            headers         jsonb
        )
        """,
        $"CREATE UNIQUE INDEX IF NOT EXISTS ux_{o.EventsTable}_aggregate_version ON {o.Qualified(o.EventsTable)} (aggregate_type, aggregate_id, version)",

        $"""
        CREATE TABLE IF NOT EXISTS {o.Qualified(o.VersionsTable)} (
            aggregate_type  varchar(200) NOT NULL,
            aggregate_id    varchar(200) NOT NULL,
            version         integer NOT NULL,
            PRIMARY KEY (aggregate_type, aggregate_id)
        )
        """,

        $"""
        CREATE TABLE IF NOT EXISTS {o.Qualified(o.SnapshotsTable)} (
            aggregate_type  varchar(200) NOT NULL,
            aggregate_id    varchar(200) NOT NULL,
            stream_version  integer NOT NULL,
            schema_version  integer NOT NULL,
            payload         bytea NOT NULL,
            compression     integer NOT NULL,
            created_utc     timestamptz NOT NULL,
            PRIMARY KEY (aggregate_type, aggregate_id)
        )
        """,

        $$"""
        CREATE TABLE IF NOT EXISTS {{o.Qualified(o.OutboxTable)}} (
            id              uuid PRIMARY KEY,
            payload_json    text NOT NULL,
            metadata_json   text NOT NULL,
            created_at      timestamptz NOT NULL,
            dispatched_at   timestamptz,
            locked_until    timestamptz,
            deliveries_json jsonb NOT NULL DEFAULT '{}'
        )
        """,
        // Dequeue filters dispatched_at IS NULL (+ residual lease check) and orders by
        // created_at: a partial index on exactly that predicate keeps the index tiny
        // (only pending envelopes) and serves the sort. The old 3-column index could
        // not back the sort past the OR on locked_until — drop it.
        $"DROP INDEX IF EXISTS {o.Schema}.ix_{o.OutboxTable}_dequeue",
        $"CREATE INDEX IF NOT EXISTS ix_{o.OutboxTable}_pending ON {o.Qualified(o.OutboxTable)} (created_at) INCLUDE (locked_until) WHERE dispatched_at IS NULL",

        $"""
        CREATE TABLE IF NOT EXISTS {o.Qualified(o.DeadLetterTable)} (
            id              varchar(300) PRIMARY KEY,
            event_id        uuid NOT NULL,
            subscriber      varchar(200) NOT NULL,
            payload_json    text NOT NULL,
            metadata_json   text NOT NULL,
            created_at      timestamptz NOT NULL,
            attempt_count   integer NOT NULL,
            failed_at       timestamptz NOT NULL,
            last_error      text NOT NULL
        )
        """
    ];

    public string InsertVersionIfAbsent(SqlEventStoreOptions o) =>
        $"INSERT INTO {o.Qualified(o.VersionsTable)} (aggregate_type, aggregate_id, version) " +
        "VALUES (@aggregate_type, @aggregate_id, @version) ON CONFLICT DO NOTHING";

    public string UpsertSnapshot(SqlEventStoreOptions o) =>
        $"INSERT INTO {o.Qualified(o.SnapshotsTable)} (aggregate_type, aggregate_id, stream_version, schema_version, payload, compression, created_utc) " +
        "VALUES (@aggregate_type, @aggregate_id, @stream_version, @schema_version, @payload, @compression, @created_utc) " +
        "ON CONFLICT (aggregate_type, aggregate_id) DO UPDATE SET " +
        "stream_version = EXCLUDED.stream_version, schema_version = EXCLUDED.schema_version, payload = EXCLUDED.payload, " +
        "compression = EXCLUDED.compression, created_utc = EXCLUDED.created_utc";

    public string LeaseOutboxBatch(SqlEventStoreOptions o) =>
        $"""
        UPDATE {o.Qualified(o.OutboxTable)} SET locked_until = @locked_until
        WHERE id IN (
            SELECT id FROM {o.Qualified(o.OutboxTable)}
            WHERE dispatched_at IS NULL AND (locked_until IS NULL OR locked_until < @now)
            ORDER BY created_at
            LIMIT @max_count
            FOR UPDATE SKIP LOCKED
        )
        RETURNING id, payload_json, metadata_json, created_at, deliveries_json::text
        """;

    public bool IsDuplicateKey(DbException exception)
        => exception is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    public string JsonParameter(string placeholder) => $"{placeholder}::jsonb";

    public string JsonToText(string column) => $"{column}::text";

    public string DeleteCompletedOutbox(SqlEventStoreOptions o) =>
        $"DELETE FROM {o.Qualified(o.OutboxTable)} WHERE id IN (" +
        $"SELECT id FROM {o.Qualified(o.OutboxTable)} WHERE dispatched_at IS NOT NULL AND dispatched_at < @cutoff LIMIT @batch)";
}
