// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Dapper;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using Aspire.Dashboard.Utils;
using Microsoft.Data.Sqlite;

namespace Aspire.Dashboard.ServiceClient;

/// <summary>
/// Creates consistently configured connections to a dashboard run database.
/// </summary>
public sealed class DashboardSqliteDatabase : IDisposable
{
    private const string SchemaResourcePrefix = "Aspire.Dashboard.ServiceClient.DatabaseSchema.";

    internal const int SchemaVersion = 17;

    private static readonly Lazy<IReadOnlyList<string>> s_schemaScripts = new(LoadSchemaScripts);

    private readonly string _connectionString;
    private readonly ActivitySource _activitySource = new(TracingSqliteConnection.ActivitySourceName);
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
            ForeignKeys = true,
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
    /// Gets the lock that serializes writes to this database.
    /// </summary>
    internal AsyncLock WriteLock { get; } = new();

    /// <summary>
    /// Determines whether a dashboard database uses the current schema version.
    /// </summary>
    /// <param name="databasePath">The path to the dashboard database.</param>
    /// <returns><see langword="true"/> when the database is compatible; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="SqliteException">The database schema version could not be read.</exception>
    public static bool IsCompatible(string databasePath)
    {
        if (!File.Exists(databasePath))
        {
            return false;
        }

        using var database = new DashboardSqliteDatabase(databasePath, readOnly: true, pooling: false);
        using var connection = database.OpenConnection();
        return ValidateSchemaVersion(connection, transaction: null, SchemaVersion);
    }

    internal TracingSqliteConnection OpenConnection()
    {
        var connection = new TracingSqliteConnection(_connectionString, DatabasePath, _activitySource);
        connection.Open();

        // synchronous is scoped to the native connection and is not stored in the database. A pooled native
        // connection retains its value, but Microsoft.Data.Sqlite doesn't expose whether Open created a new
        // native connection or leased one from the pool, so apply it after every logical open. NORMAL avoids
        // syncing the WAL after every commit while preserving database consistency, although a power loss can
        // discard the most recent transactions.
        // See https://sqlite.org/pragma.html#pragma_synchronous.
        connection.ConfigureSynchronousNormal();

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
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    public async Task InitializeSchemaAsync(CancellationToken cancellationToken)
    {
        EnsureWritable("Historical dashboard data is read-only.");

        using (await WriteLock.LockAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_schemaInitialized)
            {
                return;
            }

            using var connection = OpenConnection();
            // Unlike synchronous, WAL journal mode is stored in the database and persists across connections
            // and process restarts, so it only needs to be set during database initialization rather than on
            // every open. WAL appends writes sequentially and allows readers to continue while a writer commits.
            // See https://sqlite.org/pragma.html#pragma_journal_mode.
            connection.Execute("PRAGMA journal_mode = WAL;");

            var schemaTableExists = connection.QuerySingle<long>("""
                SELECT COUNT(*)
                FROM sqlite_schema
                WHERE type = 'table' AND name = 'dashboard_schema';
                """) != 0;
            if (schemaTableExists)
            {
                var existingSchemaVersion = GetSchemaVersion(connection, transaction: null);
                if (existingSchemaVersion != SchemaVersion)
                {
                    throw new InvalidOperationException($"The dashboard database schema version {FormatSchemaVersion(existingSchemaVersion)} does not match the expected version {SchemaVersion}.");
                }
            }

            using var transaction = connection.BeginTransaction();
            foreach (var script in s_schemaScripts.Value)
            {
                connection.Execute(script, new { SchemaVersion }, transaction);
            }

            var initializedSchemaVersion = GetSchemaVersion(connection, transaction);
            if (initializedSchemaVersion != SchemaVersion)
            {
                throw new InvalidOperationException($"The dashboard database schema was initialized to version {FormatSchemaVersion(initializedSchemaVersion)} instead of the expected version {SchemaVersion}.");
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
        // Opening the database and setting WAL creates a valid SQLite file before the schema transaction
        // commits, so an interrupted first initialization leaves a file with no dashboard_schema table.
        // Querying it directly throws "no such table: dashboard_schema", and because Resume only replaces
        // the database when this returns false, every later start would keep crashing.
        //
        // Probe sqlite_master first so a missing schema table reports "not compatible" while genuine IO,
        // locking, and corruption failures still surface as exceptions.
        // See https://www.sqlite.org/schematab.html
        var schemaTableCount = connection.QuerySingle<long>("""
            SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'dashboard_schema';
            """, transaction: transaction);
        if (schemaTableCount == 0)
        {
            return false;
        }

        return GetSchemaVersion(connection, transaction) == expectedVersion;
    }

    private static int? GetSchemaVersion(SqliteConnection connection, IDbTransaction? transaction)
    {
        return connection.QuerySingleOrDefault<int?>("""
            SELECT CASE
                WHEN COUNT(*) = 1 THEN MAX(version)
                ELSE NULL
            END
            FROM dashboard_schema;
            """, transaction: transaction);
    }

    private static string FormatSchemaVersion(int? version) => version?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
}
