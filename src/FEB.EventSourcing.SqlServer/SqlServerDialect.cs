using System.Data.Common;
using FEB.EventSourcing.Sql;
using Microsoft.Data.SqlClient;

namespace FEB.EventSourcing.SqlServer;

public sealed class SqlServerDialect : ISqlDialect
{
    public string Name => "sqlserver";

    public DbConnection CreateConnection(string connectionString) => new SqlConnection(connectionString);

    public IReadOnlyList<string> GetSchemaStatements(SqlEventStoreOptions o) =>
    [
        $"IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = '{o.Schema}') EXEC('CREATE SCHEMA [{o.Schema}]')",

        $"""
        IF OBJECT_ID('{o.Qualified(o.EventsTable)}', 'U') IS NULL
        CREATE TABLE {o.Qualified(o.EventsTable)} (
            event_id        uniqueidentifier NOT NULL PRIMARY KEY NONCLUSTERED,
            aggregate_type  nvarchar(200) NOT NULL,
            aggregate_id    nvarchar(200) NOT NULL,
            version         int NOT NULL,
            event_type      nvarchar(1000) NOT NULL,
            payload         nvarchar(max) NOT NULL,
            occurred_at     datetime2 NOT NULL,
            tenant_id       nvarchar(200) NULL,
            user_id         nvarchar(200) NULL,
            correlation_id  nvarchar(200) NOT NULL,
            causation_id    nvarchar(200) NULL,
            headers         nvarchar(max) NULL
        )
        """,
        $"""
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ux_{o.EventsTable}_aggregate_version' AND object_id = OBJECT_ID('{o.Qualified(o.EventsTable)}'))
        CREATE UNIQUE CLUSTERED INDEX ux_{o.EventsTable}_aggregate_version ON {o.Qualified(o.EventsTable)} (aggregate_type, aggregate_id, version)
        """,

        $"""
        IF OBJECT_ID('{o.Qualified(o.VersionsTable)}', 'U') IS NULL
        CREATE TABLE {o.Qualified(o.VersionsTable)} (
            aggregate_type  nvarchar(200) NOT NULL,
            aggregate_id    nvarchar(200) NOT NULL,
            version         int NOT NULL,
            CONSTRAINT pk_{o.VersionsTable} PRIMARY KEY (aggregate_type, aggregate_id)
        )
        """,

        $"""
        IF OBJECT_ID('{o.Qualified(o.SnapshotsTable)}', 'U') IS NULL
        CREATE TABLE {o.Qualified(o.SnapshotsTable)} (
            aggregate_type  nvarchar(200) NOT NULL,
            aggregate_id    nvarchar(200) NOT NULL,
            stream_version  int NOT NULL,
            schema_version  int NOT NULL,
            payload         varbinary(max) NOT NULL,
            compression     int NOT NULL,
            created_utc     datetime2 NOT NULL,
            CONSTRAINT pk_{o.SnapshotsTable} PRIMARY KEY (aggregate_type, aggregate_id)
        )
        """,

        $$"""
        IF OBJECT_ID('{{o.Qualified(o.OutboxTable)}}', 'U') IS NULL
        CREATE TABLE {{o.Qualified(o.OutboxTable)}} (
            id              uniqueidentifier NOT NULL PRIMARY KEY,
            payload_json    nvarchar(max) NOT NULL,
            metadata_json   nvarchar(max) NOT NULL,
            created_at      datetime2 NOT NULL,
            dispatched_at   datetime2 NULL,
            locked_until    datetime2 NULL,
            deliveries_json nvarchar(max) NOT NULL DEFAULT '{}'
        )
        """,
        // Dequeue filters dispatched_at IS NULL (+ residual lease check) and orders by
        // created_at: a filtered index on exactly that predicate keeps the index tiny
        // (only pending envelopes) and serves the sort. The old 3-column index could
        // not back the sort past the OR on locked_until — drop it.
        $"""
        IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_{o.OutboxTable}_dequeue' AND object_id = OBJECT_ID('{o.Qualified(o.OutboxTable)}'))
        DROP INDEX ix_{o.OutboxTable}_dequeue ON {o.Qualified(o.OutboxTable)}
        """,
        $"""
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_{o.OutboxTable}_pending' AND object_id = OBJECT_ID('{o.Qualified(o.OutboxTable)}'))
        CREATE INDEX ix_{o.OutboxTable}_pending ON {o.Qualified(o.OutboxTable)} (created_at) INCLUDE (locked_until) WHERE dispatched_at IS NULL
        """,

        $"""
        IF OBJECT_ID('{o.Qualified(o.DeadLetterTable)}', 'U') IS NULL
        CREATE TABLE {o.Qualified(o.DeadLetterTable)} (
            id              nvarchar(300) NOT NULL PRIMARY KEY,
            event_id        uniqueidentifier NOT NULL,
            subscriber      nvarchar(200) NOT NULL,
            payload_json    nvarchar(max) NOT NULL,
            metadata_json   nvarchar(max) NOT NULL,
            created_at      datetime2 NOT NULL,
            attempt_count   int NOT NULL,
            failed_at       datetime2 NOT NULL,
            last_error      nvarchar(max) NOT NULL
        )
        """
    ];

    public string InsertVersionIfAbsent(SqlEventStoreOptions o) =>
        $"""
        INSERT INTO {o.Qualified(o.VersionsTable)} (aggregate_type, aggregate_id, version)
        SELECT @aggregate_type, @aggregate_id, @version
        WHERE NOT EXISTS (SELECT 1 FROM {o.Qualified(o.VersionsTable)} WITH (UPDLOCK, HOLDLOCK) WHERE aggregate_type = @aggregate_type AND aggregate_id = @aggregate_id)
        """;

    public string UpsertSnapshot(SqlEventStoreOptions o) =>
        $"""
        UPDATE {o.Qualified(o.SnapshotsTable)} SET stream_version = @stream_version, schema_version = @schema_version, payload = @payload, compression = @compression, created_utc = @created_utc
        WHERE aggregate_type = @aggregate_type AND aggregate_id = @aggregate_id;
        IF @@ROWCOUNT = 0
        INSERT INTO {o.Qualified(o.SnapshotsTable)} (aggregate_type, aggregate_id, stream_version, schema_version, payload, compression, created_utc)
        VALUES (@aggregate_type, @aggregate_id, @stream_version, @schema_version, @payload, @compression, @created_utc);
        """;

    public string LeaseOutboxBatch(SqlEventStoreOptions o) =>
        $"""
        UPDATE t SET locked_until = @locked_until
        OUTPUT inserted.id, inserted.payload_json, inserted.metadata_json, inserted.created_at, inserted.deliveries_json
        FROM (
            SELECT TOP (@max_count) * FROM {o.Qualified(o.OutboxTable)} WITH (READPAST, UPDLOCK, ROWLOCK)
            WHERE dispatched_at IS NULL AND (locked_until IS NULL OR locked_until < @now)
            ORDER BY created_at
        ) t
        """;

    public bool IsDuplicateKey(DbException exception)
        => exception is SqlException { Number: 2601 or 2627 };

    public string JsonParameter(string placeholder) => placeholder;

    public string JsonToText(string column) => column;

    public string DeleteCompletedOutbox(SqlEventStoreOptions o) =>
        $"DELETE TOP (@batch) FROM {o.Qualified(o.OutboxTable)} WHERE dispatched_at IS NOT NULL AND dispatched_at < @cutoff";
}
