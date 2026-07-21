// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Dapper;
using System.Data;
using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace Aspire.Dashboard.ServiceClient;

/// <summary>
/// Creates consistently configured connections to a dashboard run database.
/// </summary>
public sealed class DashboardSqliteDatabase : IDisposable
{
    private const string SchemaResourcePrefix = "Aspire.Dashboard.ServiceClient.DatabaseSchema.";

    internal const int SchemaVersion = 12;

    private static readonly Lazy<IReadOnlyList<string>> s_schemaScripts = new(LoadSchemaScripts);

    private readonly string _connectionString;
    private readonly ActivitySource _activitySource = new(TracingSqliteConnection.ActivitySourceName);
    private readonly object _schemaLock = new();
    private bool _schemaInitialized;

    /// <summary>
    /// Initializes a new instance of the <see cref="DashboardSqliteDatabase"/> class.
    /// </summary>
    /// <param name="databasePath">The path to the dashboard database.</param>
    /// <param name="readOnly">A value indicating whether the database is opened for read-only access.</param>
    /// <param name="pooling">A value indicating whether SQLite connection pooling is enabled.</param>
    public DashboardSqliteDatabase(string databasePath, bool readOnly = false, bool pooling = true)
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
            Pooling = pooling,
            DefaultTimeout = 5
        }.ToString();
    }

    /// <summary>
    /// Gets the full path to the dashboard database.
    /// </summary>
    public string DatabasePath { get; }

    /// <summary>
    /// Gets a value indicating whether the database is opened for read-only access.
    /// </summary>
    public bool IsReadOnly { get; }

    internal ActivitySource ActivitySource => _activitySource;

    /// <summary>
    /// Determines whether a dashboard database uses the current schema version.
    /// </summary>
    /// <param name="databasePath">The path to the dashboard database.</param>
    /// <returns><see langword="true"/> when the database is compatible; otherwise, <see langword="false"/>.</returns>
    public static bool IsCompatible(string databasePath)
    {
        if (!File.Exists(databasePath))
        {
            return false;
        }

        using var database = new DashboardSqliteDatabase(databasePath, readOnly: true, pooling: false);
        try
        {
            using var connection = database.OpenConnection();
            return ValidateSchemaVersion(connection, transaction: null, SchemaVersion);
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    internal TracingSqliteConnection OpenConnection()
    {
        var connection = new TracingSqliteConnection(_connectionString, DatabasePath, _activitySource);
        connection.Open();
        connection.Execute("PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;");

        return connection;
    }

    internal bool ValidateSchemaVersion(int metadataSchemaVersion)
    {
        using var connection = OpenConnection();
        return ValidateSchemaVersion(connection, transaction: null, metadataSchemaVersion);
    }

    /// <summary>
    /// Clears pooled SQLite connections associated with this database.
    /// </summary>
    public void ClearPool()
    {
        using var connection = new SqliteConnection(_connectionString);
        SqliteConnection.ClearPool(connection);
    }

    public void Dispose() => _activitySource.Dispose();

    /// <summary>
    /// Initializes the dashboard database schema when it has not already been initialized.
    /// </summary>
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
            if (schemaTableExists && !ValidateSchemaVersion(connection, transaction: null, SchemaVersion))
            {
                throw new InvalidOperationException("The dashboard database schema does not match the expected version.");
            }

            using var transaction = connection.BeginTransaction();
            foreach (var script in s_schemaScripts.Value)
            {
                connection.Execute(script, new { SchemaVersion }, transaction);
            }

            if (!ValidateSchemaVersion(connection, transaction, SchemaVersion))
            {
                throw new InvalidOperationException("The dashboard database schema was not initialized to the expected version.");
            }
            transaction.Commit();
            _schemaInitialized = true;
        }
    }

    /// <summary>
    /// Throws an exception with the specified message when the database is read-only.
    /// </summary>
    /// <param name="message">The exception message used when the database is read-only.</param>
    /// <exception cref="InvalidOperationException">The database is read-only.</exception>
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

    private static bool ValidateSchemaVersion(SqliteConnection connection, IDbTransaction? transaction, int expectedVersion)
    {
        var version = connection.QuerySingleOrDefault<int?>("""
            SELECT CASE
                WHEN COUNT(*) = 1 THEN MAX(version)
                ELSE NULL
            END
            FROM dashboard_schema;
            """, transaction: transaction);
        return version == expectedVersion;
    }
}
