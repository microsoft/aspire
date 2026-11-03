// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Aspire.Dashboard.Model;

/// <summary>
/// Stores pinned metric instruments in the application settings database, independently of individual runs.
/// </summary>
/// <remarks>
/// New pins are scoped to the resource instance that reports the instrument. Pins saved by dashboard versions
/// before resource scoping are retained as global pins and continue to match every resource that reports the
/// instrument.
/// </remarks>
public sealed class MetricPinStore
{
    private const string SettingsName = "pinned metrics";

    private readonly Lock _lock = new();
    private readonly List<MetricPin> _pins = [];
    private readonly DashboardSettingsDatabase _database;
    private bool _loaded;

    public MetricPinStore(IOptions<DashboardOptions> options, ILogger<MetricPinStore> logger)
        : this(DashboardSettingsDatabase.GetDatabasePath(options.Value), logger)
    {
    }

    internal MetricPinStore(string? databasePath, ILogger logger)
    {
        _database = new DashboardSettingsDatabase(databasePath, logger);
    }

    /// <summary>
    /// Raised after pins change. Handlers may run on any thread.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Gets the pinned instruments in the order they were pinned.
    /// </summary>
    public IReadOnlyList<MetricPin> GetPins()
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
        var instrumentList = instruments.ToList();
        return [.. GetPins().SelectMany(pin => instrumentList.Where(instrument => Matches(pin, instrument))).Distinct()];
    }

    public bool IsPinned(ResourceKey resourceKey, OtlpInstrumentKey instrumentKey)
    {
        lock (_lock)
        {
            EnsureLoaded();
            return _pins.Any(pin => Matches(pin, resourceKey, instrumentKey));
        }
    }

    /// <summary>
    /// Pins or unpins an instrument. Returns <see langword="false"/> if the change couldn't be saved,
    /// in which case the pins are unchanged.
    /// </summary>
    public bool SetPinned(ResourceKey resourceKey, OtlpInstrumentKey instrumentKey, bool pinned)
    {
        lock (_lock)
        {
            EnsureLoaded();
            var resourcePin = new MetricPin(resourceKey, instrumentKey);
            var existingPin = _pins.FirstOrDefault(pin => Matches(pin, resourceKey, instrumentKey));
            if ((existingPin is not null) == pinned)
            {
                return true;
            }

            // Unpinning an existing global pin removes that global record instead of silently replacing it with
            // resource-scoped state. A later pin creates a new resource-scoped record.
            var changedPin = pinned ? resourcePin : existingPin!;
            if (!Save(changedPin, pinned))
            {
                return false;
            }

            if (pinned)
            {
                _pins.Add(changedPin);
            }
            else
            {
                _pins.Remove(changedPin);
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

        _database.Load(SettingsName, connection =>
        {
            EnsureSchema(connection);

            using var loadCommand = connection.CreateCommand();
            // pin_id increases with each insert, so ordering by it lists pins in the order they were pinned.
            loadCommand.CommandText = """
                SELECT resource_name, resource_instance_id, meter_name, instrument_name
                FROM metric_pins
                ORDER BY pin_id;
                """;
            using var reader = loadCommand.ExecuteReader();
            while (reader.Read())
            {
                var resourceKey = reader.IsDBNull(0)
                    ? (ResourceKey?)null
                    : new ResourceKey(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
                _pins.Add(new MetricPin(resourceKey, new OtlpInstrumentKey(reader.GetString(2), reader.GetString(3))));
            }
        });
    }

    private static void EnsureSchema(SqliteConnection connection)
    {
        using var tableExistsCommand = connection.CreateCommand();
        tableExistsCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'metric_pins';";
        var tableExists = (long)tableExistsCommand.ExecuteScalar()! > 0;
        if (!tableExists)
        {
            CreateTable(connection);
            return;
        }

        using var columnsCommand = connection.CreateCommand();
        columnsCommand.CommandText = "PRAGMA table_info(metric_pins);";
        using var reader = columnsCommand.ExecuteReader();
        var hasResourceName = false;
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), "resource_name", StringComparison.Ordinal))
            {
                hasResourceName = true;
                break;
            }
        }

        if (hasResourceName)
        {
            return;
        }
        reader.Close();

        using var transaction = connection.BeginTransaction();
        using var migrationCommand = connection.CreateCommand();
        migrationCommand.Transaction = transaction;
        // Existing rows have no resource identity. Preserve them as explicit global pins rather than assigning
        // them to whichever matching resource happens to report telemetry first.
        migrationCommand.CommandText = """
            ALTER TABLE metric_pins RENAME TO metric_pins_global;
            CREATE TABLE metric_pins (
                pin_id INTEGER NOT NULL PRIMARY KEY,
                resource_name TEXT NULL,
                resource_instance_id TEXT NULL,
                meter_name TEXT NOT NULL,
                instrument_name TEXT NOT NULL
            );
            CREATE UNIQUE INDEX metric_pins_identity ON metric_pins (
                IFNULL(resource_name, ''),
                IFNULL(resource_instance_id, ''),
                meter_name,
                instrument_name
            );
            INSERT INTO metric_pins (pin_id, resource_name, resource_instance_id, meter_name, instrument_name)
                SELECT pin_id, NULL, NULL, meter_name, instrument_name FROM metric_pins_global;
            DROP TABLE metric_pins_global;
            """;
        migrationCommand.ExecuteNonQuery();
        transaction.Commit();
    }

    private static void CreateTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE metric_pins (
                pin_id INTEGER NOT NULL PRIMARY KEY,
                resource_name TEXT NULL,
                resource_instance_id TEXT NULL,
                meter_name TEXT NOT NULL,
                instrument_name TEXT NOT NULL
            );
            CREATE UNIQUE INDEX metric_pins_identity ON metric_pins (
                IFNULL(resource_name, ''),
                IFNULL(resource_instance_id, ''),
                meter_name,
                instrument_name
            );
            """;
        command.ExecuteNonQuery();
    }

    private bool Save(MetricPin pin, bool pinned)
    {
        return _database.TrySave(SettingsName, connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = pinned
                ? """
                    INSERT OR IGNORE INTO metric_pins (resource_name, resource_instance_id, meter_name, instrument_name)
                    VALUES ($resource_name, $resource_instance_id, $meter, $instrument);
                    """
                : """
                    DELETE FROM metric_pins
                    WHERE resource_name IS $resource_name
                        AND resource_instance_id IS $resource_instance_id
                        AND meter_name = $meter
                        AND instrument_name = $instrument;
                    """;
            command.Parameters.AddWithValue("$resource_name", (object?)pin.ResourceKey?.Name ?? DBNull.Value);
            command.Parameters.AddWithValue("$resource_instance_id", (object?)pin.ResourceKey?.InstanceId ?? DBNull.Value);
            command.Parameters.AddWithValue("$meter", pin.InstrumentKey.MeterName);
            command.Parameters.AddWithValue("$instrument", pin.InstrumentKey.InstrumentName);
            command.ExecuteNonQuery();
        });
    }

    private static bool Matches(MetricPin pin, OtlpInstrumentSummary instrument) =>
        Matches(pin, instrument.ResourceView.ResourceKey, instrument.GetKey());

    private static bool Matches(MetricPin pin, ResourceKey resourceKey, OtlpInstrumentKey instrumentKey) =>
        pin.InstrumentKey == instrumentKey && (pin.ResourceKey is null || pin.ResourceKey == resourceKey);
}

public sealed record MetricPin(ResourceKey? ResourceKey, OtlpInstrumentKey InstrumentKey);
