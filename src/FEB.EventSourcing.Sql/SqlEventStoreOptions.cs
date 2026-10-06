using FEB.EventSourcing.Snapshots;

namespace FEB.EventSourcing.Sql;

public sealed class SqlEventStoreOptions
{
    internal string ConnectionString { get; private set; } = null!;

    /// <summary>Database schema (Postgres: schema, SQL Server: schema). Default "es".</summary>
    public string Schema { get; private set; } = "es";

    internal bool SnapshotsEnabled { get; private set; }

    internal bool EnsureSchemaOnStartup { get; private set; } = true;

    internal SnapshotCompression Compression { get; private set; } = SnapshotCompression.None;

    // Table names (unqualified) — one set of tables for all aggregate types,
    // discriminated by the aggregate_type column. Public for dialect implementations.
    public string EventsTable => "events";
    public string VersionsTable => "aggregate_versions";
    public string SnapshotsTable => "snapshots";
    public string OutboxTable => "outbox";
    public string DeadLetterTable => "outbox_deadletter";

    public void SetConnectionString(string connectionString) => ConnectionString = connectionString;

    public void SetSchema(string schema)
    {
        if (string.IsNullOrWhiteSpace(schema) || schema.Any(c => !char.IsLetterOrDigit(c) && c != '_'))
            throw new ArgumentException("Schema must be a simple identifier (letters, digits, underscore).", nameof(schema));
        Schema = schema;
    }

    public void EnableSnapshots() => SnapshotsEnabled = true;

    public void SetCompression(SnapshotCompression compression) => Compression = compression;

    /// <summary>Disable the startup schema/index initializer (e.g. when DDL is managed externally).</summary>
    public void DisableSchemaInitialization() => EnsureSchemaOnStartup = false;

    /// <summary>Schema-qualified table name for dialect SQL.</summary>
    public string Qualified(string table) => $"{Schema}.{table}";
}
