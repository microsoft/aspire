// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Dapper;
using Microsoft.Data.Sqlite;

namespace Aspire.Dashboard.ServiceClient;

/// <summary>
/// Creates consistently configured connections to a dashboard run database.
/// </summary>
internal sealed class DashboardSqliteDatabase
{
    private const string SchemaResourcePrefix = "Aspire.Dashboard.ServiceClient.DatabaseSchema.";

    internal const int SchemaVersion = 7;
    internal const string OrdinalIgnoreCaseCollation = "ORDINAL_IGNORE_CASE";
    internal const string OrdinalContainsFunction = "ordinal_contains";
    internal const string OrdinalStartsWithFunction = "ordinal_starts_with";

    private static readonly Lazy<IReadOnlyList<string>> s_schemaScripts = new(LoadSchemaScripts);

    private readonly string _connectionString;
    private readonly object _schemaLock = new();
    private bool _schemaInitialized;

    public DashboardSqliteDatabase(string databasePath, bool readOnly = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        DatabasePath = Path.GetFullPath(databasePath);
        IsReadOnly = readOnly;

        if (!readOnly)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString();
    }

    public string DatabasePath { get; }

    public bool IsReadOnly { get; }

    public static bool IsCompatible(string databasePath)
    {
        if (!File.Exists(databasePath))
        {
            return false;
        }

        try
        {
            var database = new DashboardSqliteDatabase(databasePath, readOnly: true);
            using var connection = database.OpenConnection();
            var version = connection.QuerySingleOrDefault<int?>("SELECT version FROM dashboard_schema;");
            return version == SchemaVersion;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.CreateCollation(
            OrdinalIgnoreCaseCollation,
            (left, right) => string.Compare(left, right, StringComparison.OrdinalIgnoreCase));
        connection.CreateFunction<string?, string?, bool>(
            OrdinalContainsFunction,
            (value, fragment) => value?.Contains(fragment ?? string.Empty, StringComparison.OrdinalIgnoreCase) ?? false,
            isDeterministic: true);
        connection.CreateFunction<string?, string?, bool>(
            OrdinalStartsWithFunction,
            (value, prefix) => value?.StartsWith(prefix ?? string.Empty, StringComparison.OrdinalIgnoreCase) ?? false,
            isDeterministic: true);
        connection.Open();
        connection.Execute("PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;");

        return connection;
    }

    public void InitializeSchema()
    {
        EnsureWritable("Historical dashboard data is read-only.");

        lock (_schemaLock)
        {
            if (_schemaInitialized)
            {
                return;
            }

            using var connection = OpenConnection();
            connection.Execute("PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;");

            var schemaTableExists = connection.QuerySingle<long>("""
                SELECT COUNT(*)
                FROM sqlite_schema
                WHERE type = 'table' AND name = 'dashboard_schema';
                """) != 0;
            if (schemaTableExists)
            {
                ValidateSchemaVersion(connection, transaction: null);
            }

            using var transaction = connection.BeginTransaction();
            foreach (var script in s_schemaScripts.Value)
            {
                connection.Execute(script, new { SchemaVersion }, transaction);
            }

            ValidateSchemaVersion(connection, transaction);
            transaction.Commit();
            _schemaInitialized = true;
        }
    }

    public void EnsureWritable(string message)
    {
        if (IsReadOnly)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static IReadOnlyList<string> LoadSchemaScripts()
    {
        var assembly = typeof(DashboardSqliteDatabase).Assembly;
        // Numeric filename prefixes define execution order because later schema domains reference tables created by earlier scripts.
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(SchemaResourcePrefix, StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        if (resourceNames.Length == 0)
        {
            throw new InvalidOperationException("No embedded dashboard database schema scripts were found.");
        }

        var scripts = new List<string>(resourceNames.Length);
        foreach (var resourceName in resourceNames)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded dashboard database schema script '{resourceName}' was not found.");
            using var reader = new StreamReader(stream);
            scripts.Add(reader.ReadToEnd());
        }

        return scripts;
    }

    private static void ValidateSchemaVersion(SqliteConnection connection, SqliteTransaction? transaction)
    {
        var version = connection.QuerySingle<int>("SELECT version FROM dashboard_schema;", transaction: transaction);
        if (version != SchemaVersion)
        {
            throw new InvalidOperationException($"Unsupported dashboard database schema version '{version}'.");
        }
    }
}