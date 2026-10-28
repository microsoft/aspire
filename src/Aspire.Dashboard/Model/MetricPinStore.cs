// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Aspire.Dashboard.Model;

/// <summary>
/// Stores pinned metric instruments in the application settings database, independently of individual runs.
/// </summary>
/// <remarks>
/// Pins are keyed by meter and instrument name, not by resource. Pinning <c>http.server.request.duration</c>
/// once pins it for every service that reports it, which matches how the same instrument is compared across
/// services, and keeps pins valid when resources are renamed or replicas get new instance names.
/// </remarks>
public sealed class MetricPinStore
{
    private readonly Lock _lock = new();
    private readonly ILogger _logger;
    private readonly List<OtlpInstrumentKey> _pins = [];
    private readonly string? _databasePath;
    private readonly string? _connectionString;
    private bool _loaded;
    private bool _loadFailed;

    public MetricPinStore(IOptions<DashboardOptions> options, ILogger<MetricPinStore> logger)
        : this(ResourceTagStore.GetDatabasePath(options.Value), logger)
    {
    }

    internal MetricPinStore(string? databasePath, ILogger logger)
    {
        _databasePath = databasePath;
        _logger = logger;
        if (databasePath is not null)
        {
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                // Pins change rarely. Not pooling avoids keeping the settings file open between changes.
                Pooling = false,
                DefaultTimeout = 5
            }.ToString();
        }
    }

    /// <summary>
    /// Raised after pins change. Handlers may run on any thread.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Gets the pinned instruments in the order they were pinned.
    /// </summary>
    public IReadOnlyList<OtlpInstrumentKey> GetPins()
    {
        lock (_lock)
        {
            EnsureLoaded();
            return [.. _pins];
        }
    }

    /// <summary>
    /// Gets the pinned instruments among <paramref name="instruments"/>, in the order they were pinned.
    /// Pins for instruments that aren't in the list, such as ones only another resource reports, are skipped.
    /// </summary>
    public List<OtlpInstrumentSummary> GetPinnedInstruments(IEnumerable<OtlpInstrumentSummary> instruments)
    {
        // The same instrument can come from several resources, and each of them is pinned along with it.
        var instrumentsByKey = instruments.ToLookup(i => i.GetKey());
        return [.. GetPins().SelectMany(key => instrumentsByKey[key])];
    }

    public bool IsPinned(OtlpInstrumentKey key)
    {
        lock (_lock)
        {
            EnsureLoaded();
            return _pins.Contains(key);
        }
    }

    /// <summary>
    /// Pins or unpins an instrument. Returns <see langword="false"/> if the change couldn't be saved,
    /// in which case the pins are unchanged.
    /// </summary>
    public bool SetPinned(OtlpInstrumentKey key, bool pinned)
    {
        lock (_lock)
        {
            EnsureLoaded();
            if (_pins.Contains(key) == pinned)
            {
                return true;
            }
            if (!Save(key, pinned))
            {
                return false;
            }

            if (pinned)
            {
                _pins.Add(key);
            }
            else
            {
                _pins.Remove(key);
            }
        }

        Changed?.Invoke();
        return true;
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }
        _loaded = true;
        if (_connectionString is null)
        {
            return;
        }

        try
        {
            DirectoryHelper.CreateWithOwnerOnlyPermissions(Path.GetDirectoryName(_databasePath)!);
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            // pin_id is the rowid, which increases with each insert, so ordering by it lists pins in the
            // order they were pinned.
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS metric_pins (
                    pin_id INTEGER NOT NULL PRIMARY KEY,
                    meter_name TEXT NOT NULL,
                    instrument_name TEXT NOT NULL,
                    UNIQUE (meter_name, instrument_name)
                );
                SELECT meter_name, instrument_name FROM metric_pins ORDER BY pin_id;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                _pins.Add(new OtlpInstrumentKey(reader.GetString(0), reader.GetString(1)));
            }
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            // Don't write over a database that couldn't be read; pinning stays unavailable for this run.
            _loadFailed = true;
            _logger.LogWarning(ex, "Unable to load pinned metrics from '{DatabasePath}'.", _databasePath);
        }
    }

    private bool Save(OtlpInstrumentKey key, bool pinned)
    {
        if (_loadFailed)
        {
            return false;
        }
        if (_connectionString is null)
        {
            return true;
        }

        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = pinned
                ? "INSERT OR IGNORE INTO metric_pins (meter_name, instrument_name) VALUES ($meter, $instrument);"
                : "DELETE FROM metric_pins WHERE meter_name = $meter AND instrument_name = $instrument;";
            command.Parameters.AddWithValue("$meter", key.MeterName);
            command.Parameters.AddWithValue("$instrument", key.InstrumentName);
            command.ExecuteNonQuery();
            return true;
        }
        catch (SqliteException ex)
        {
            _logger.LogWarning(ex, "Unable to save pinned metrics to '{DatabasePath}'.", _databasePath);
            return false;
        }
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
