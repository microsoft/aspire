// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Configuration;
using Aspire.Shared;
using Microsoft.Data.Sqlite;

namespace Aspire.Dashboard.Model;

/// <summary>
/// The application settings database, which keeps settings that describe the application rather than a single run,
/// such as resource tags, resource order and pinned metrics.
/// </summary>
/// <remarks>
/// <para>
/// The settings are shared by every run of the application, so they're stored next to the runs instead of in the
/// per-run database, which is replaced on each run and pruned. Each settings store uses its own tables in the file.
/// </para>
/// <para>
/// Settings are a convenience, so every store handles a database it can't use the same way: when the settings
/// can't be loaded, the store keeps its settings in memory for the rest of the process and never writes to the file,
/// so a database that couldn't be read is never overwritten. When the dashboard doesn't persist data, settings are
/// only kept in memory too.
/// </para>
/// </remarks>
internal sealed class DashboardSettingsDatabase
{
    internal const string DatabaseFileName = "dashboard-settings.db";

    private readonly ILogger _logger;
    private string? _connectionString;

    public DashboardSettingsDatabase(string? databasePath, ILogger logger)
    {
        DatabasePath = databasePath;
        _logger = logger;

        if (databasePath is not null)
        {
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                // Settings change rarely. Not pooling connections avoids keeping the file open between changes, so
                // the data directory can be deleted while the dashboard is running.
                Pooling = false,
                DefaultTimeout = 5
            }.ToString();
        }
    }

    /// <summary>
    /// Gets the path of the settings database, or <see langword="null"/> when settings are only kept in memory.
    /// </summary>
    public string? DatabasePath { get; }

    internal static string? GetDatabasePath(DashboardOptions options) => options.Data.PersistenceMode switch
    {
        DashboardPersistenceMode.None => null,
        _ => Path.Combine(DashboardRunStore.GetApplicationSettingsDirectory(options.Data.Directory, options.GetApplicationNameOrDefault()), DatabaseFileName)
    };

    /// <summary>
    /// Creates the store's tables and reads its settings. When that fails, the store's settings are kept in memory.
    /// </summary>
    /// <param name="settingsName">The settings, for log messages, for example "resource tags".</param>
    /// <param name="load">Reads the settings from an open connection.</param>
    public void Load(string settingsName, Action<SqliteConnection> load)
    {
        if (_connectionString is null)
        {
            return;
        }

        try
        {
            // Settings can include resource names, so restrict them to the current user like run data.
            DirectoryHelper.CreateWithOwnerOnlyPermissions(Path.GetDirectoryName(DatabasePath)!);

            using var connection = OpenConnection();
            load(connection);
        }
        catch (Exception ex) when (IsDatabaseException(ex))
        {
            _logger.LogWarning(ex, "Unable to load {SettingsName} from '{DatabasePath}'. Changes are kept in memory and won't be saved.", settingsName, DatabasePath);
            _connectionString = null;
        }
    }

    /// <summary>
    /// Saves a change to the store's settings.
    /// </summary>
    /// <param name="settingsName">The settings, for log messages, for example "resource tags".</param>
    /// <param name="save">Writes the change with an open connection.</param>
    /// <returns>
    /// <see langword="false"/> when the change couldn't be saved; otherwise, <see langword="true"/>, including when
    /// settings are only kept in memory.
    /// </returns>
    public bool TrySave(string settingsName, Action<SqliteConnection> save)
    {
        if (_connectionString is null)
        {
            return true;
        }

        try
        {
            using var connection = OpenConnection();
            save(connection);
            return true;
        }
        catch (Exception ex) when (IsDatabaseException(ex))
        {
            _logger.LogWarning(ex, "Unable to save {SettingsName} to '{DatabasePath}'.", settingsName, DatabasePath);
            return false;
        }
    }

    // The file can be locked by another process, deleted with its directory, or not accessible to the current user.
    private static bool IsDatabaseException(Exception ex) => ex is SqliteException or IOException or UnauthorizedAccessException;

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
