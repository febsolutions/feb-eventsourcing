using FEB.EventSourcing.Postgres;
using FEB.EventSourcing.Sql;

// ReSharper disable once CheckNamespace
namespace FEB.EventSourcing;

public static class PostgresEventStoreConfigurationExtensions
{
    /// <summary>
    /// PostgreSQL persistence (Npgsql). Creates schema/tables/indexes at startup unless
    /// disabled. Combine with <c>UseSqlOutbox()</c> for the relational outbox.
    /// </summary>
    public static IEventSourcingBuilder UsePostgres(
        this IEventSourcingBuilder builder,
        string connectionString,
        Action<SqlEventStoreOptions>? configure = null)
        => builder.UseSql(new PostgresDialect(), connectionString, configure);
}
