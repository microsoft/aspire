// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Utils;

namespace Aspire.Dashboard.Model;

internal static class SqlHelpers
{
    private static readonly string[] s_databaseLogSourcePrefixes =
    [
        "Microsoft.EntityFrameworkCore",
        "Npgsql",
        "MySqlConnector"
    ];

    // Whole-message highlighting is limited to command categories, not connection or transaction logs.
    private static readonly string[] s_databaseCommandLogSourcePrefixes =
    [
        "Microsoft.EntityFrameworkCore.Database.Command",
        "Npgsql.Command",
        "MySqlConnector.MySqlCommand",
        "NHibernate.SQL"
    ];

    private static readonly Dictionary<string, (string Format, bool RequiresSqlDatabase)> s_semanticQueryFields = new(StringComparer.Ordinal)
    {
        ["db.query.text"] = (DashboardUIHelpers.SqlFormat, true),
        ["db.statement"] = (DashboardUIHelpers.SqlFormat, true)
    };

    // EF Core uses commandText for the query-only portion of its database command logs.
    // Match complete field names rather than substrings such as "sql.parameters".
    private static readonly Dictionary<string, (string Format, bool RequiresSqlDatabase)> s_queryFields = new(StringComparer.OrdinalIgnoreCase)
    {
        ["commandText"] = (DashboardUIHelpers.SqlFormat, true),
        ["queryText"] = (DashboardUIHelpers.SqlFormat, true),
        ["sql"] = (DashboardUIHelpers.SqlFormat, false),
        ["sqlQuery"] = (DashboardUIHelpers.SqlFormat, false),
        ["sqlStatement"] = (DashboardUIHelpers.SqlFormat, false),
        ["sql.query"] = (DashboardUIHelpers.SqlFormat, false),
        ["sql.statement"] = (DashboardUIHelpers.SqlFormat, false)
    };

    private static readonly Dictionary<string, string> s_databaseSystemFormats = new[]
    {
        "other_sql", "microsoft.sql_server", "mssql", "mssqlcompact",
        "mysql", "mariadb", "postgresql", "sqlite", "oracle.db", "oracle",
        "ibm.db2", "db2", "ibm.informix", "informix",
        "cockroachdb", "h2database", "h2", "hsqldb", "derby",
        "sap.hana", "hana", "sap.maxdb", "maxdb",
        "sybase", "ingres", "actian.ingres", "firebirdsql", "firebird",
        "gcp.spanner", "spanner", "hive", "ibm.netezza", "netezza",
        "intersystems.cache", "cache",
        "enterprise_db", "enterprisedb", "progress", "teradata", "vertica",
        "snowflake", "trino", "presto", "clickhouse",
        "aws.redshift", "redshift", "gcp.bigquery", "bigquery"
    }.ToDictionary(static system => system, static _ => DashboardUIHelpers.SqlFormat, StringComparer.Ordinal);

    public static string? GetDatabaseSystem(KeyValuePair<string, string>[] attributes)
    {
        return attributes.GetValue("db.system.name") ?? attributes.GetValue("db.system");
    }

    public static string? GetLogMessageFormat(string source)
    {
        // Command messages can contain metadata followed by SQL, e.g.:
        //   Executed DbCommand (5ms) [Parameters=[], CommandType='Text', CommandTimeout='30']
        //   SELECT 1
        // Highlight the whole message rather than parsing version-dependent log templates.
        return s_databaseCommandLogSourcePrefixes.Any(prefix => source.StartsWith(prefix, StringComparison.Ordinal))
            ? DashboardUIHelpers.SqlFormat
            : null;
    }

    public static string? GetLogFormat(string name, string source, KeyValuePair<string, string>[] attributes)
    {
        var databaseSystem = GetDatabaseSystem(attributes);
        // Database metadata also identifies custom sources. Without it, require a known provider
        // category such as Microsoft.EntityFrameworkCore.Database.Command or Npgsql.Command.
        return !string.IsNullOrEmpty(databaseSystem) ||
            s_databaseLogSourcePrefixes.Any(prefix => source.StartsWith(prefix, StringComparison.Ordinal))
            ? GetFormat(name, databaseSystem)
            : null;
    }

    public static string? GetFormat(string name, KeyValuePair<string, string>[] attributes) => GetFormat(name, GetDatabaseSystem(attributes));

    public static string? GetFormat(string name, string? databaseSystem)
    {
        if (!s_semanticQueryFields.TryGetValue(name, out var field) &&
            !s_queryFields.TryGetValue(name, out field))
        {
            return null;
        }

        // Both current and legacy query attributes can contain non-SQL commands.
        // https://opentelemetry.io/docs/specs/semconv/registry/attributes/db/
        if (field.RequiresSqlDatabase && !string.IsNullOrEmpty(databaseSystem))
        {
            return s_databaseSystemFormats.GetValueOrDefault(databaseSystem);
        }

        return field.Format;
    }
}
