using FEB.EventSourcing.Sql;
using FEB.EventSourcing.SqlServer;

// ReSharper disable once CheckNamespace
namespace FEB.EventSourcing;

public static class SqlServerEventStoreConfigurationExtensions
{
    /// <summary>
    /// SQL Server persistence (Microsoft.Data.SqlClient). Creates schema/tables/indexes
    /// at startup unless disabled. Combine with <c>UseSqlOutbox()</c> for the relational outbox.
    /// </summary>
    public static IEventSourcingBuilder UseSqlServer(
        this IEventSourcingBuilder builder,
        string connectionString,
        Action<SqlEventStoreOptions>? configure = null)
        => builder.UseSql(new SqlServerDialect(), connectionString, configure);
}
