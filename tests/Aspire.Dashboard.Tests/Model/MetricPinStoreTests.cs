// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public sealed class MetricPinStoreTests(ITestOutputHelper testOutputHelper) : IDisposable
{
    private static readonly OtlpInstrumentKey s_duration = new("Microsoft.AspNetCore.Hosting", "http.server.request.duration");
    private static readonly OtlpInstrumentKey s_heap = new("System.Runtime", "dotnet.gc.heap.total_allocated");
    private static readonly OtlpInstrumentKey s_cpu = new("System.Runtime", "dotnet.process.cpu.time");
    private static readonly ResourceKey s_api = new("api", InstanceId: null);
    private static readonly ResourceKey s_worker = new("worker", InstanceId: null);

    private readonly TemporaryWorkspace _workspace = TemporaryWorkspace.Create(testOutputHelper);

    [Fact]
    public void SetPinned_PersistsInPinnedOrderAcrossInstances()
    {
        var databasePath = Path.Combine(_workspace.Path, DashboardSettingsDatabase.DatabaseFileName);
        var store = new MetricPinStore(databasePath, NullLogger.Instance);

        Assert.True(store.SetPinned(s_api, s_heap, pinned: true));
        Assert.True(store.SetPinned(s_worker, s_duration, pinned: true));
        Assert.True(store.SetPinned(s_api, s_cpu, pinned: true));
        Assert.True(store.SetPinned(s_worker, s_duration, pinned: false));

        // A new instance reads the saved pins, as after restarting the dashboard.
        store = new MetricPinStore(databasePath, NullLogger.Instance);
        Assert.Equal([new MetricPin(s_api, s_heap), new MetricPin(s_api, s_cpu)], store.GetPins());
        Assert.True(store.IsPinned(s_api, s_heap));
        Assert.False(store.IsPinned(s_worker, s_duration));

        // Re-pinning moves the instrument to the end because pins are listed in the order they were pinned.
        Assert.True(store.SetPinned(s_worker, s_duration, pinned: true));
        store = new MetricPinStore(databasePath, NullLogger.Instance);
        Assert.Equal(
            [new MetricPin(s_api, s_heap), new MetricPin(s_api, s_cpu), new MetricPin(s_worker, s_duration)],
            store.GetPins());
    }

    [Fact]
    public void SetPinned_RaisesChangedOnlyWhenPinsChange()
    {
        var store = new MetricPinStore(databasePath: null, NullLogger.Instance);
        var changedCount = 0;
        store.Changed += () => changedCount++;

        store.SetPinned(s_api, s_heap, pinned: true);
        store.SetPinned(s_api, s_heap, pinned: true);
        store.SetPinned(s_api, s_cpu, pinned: false);
        store.SetPinned(s_api, s_heap, pinned: false);

        Assert.Equal(2, changedCount);
        Assert.Empty(store.GetPins());
    }

    [Fact]
    public void SetPinned_UnreadableDatabase_KeepsPinsInMemory()
    {
        // A directory where the database file should be makes opening it fail.
        var databasePath = Path.Combine(_workspace.Path, DashboardSettingsDatabase.DatabaseFileName);
        Directory.CreateDirectory(databasePath);
        var store = new MetricPinStore(databasePath, NullLogger.Instance);

        // Like the other settings stores, pins keep working for the rest of the process without touching the file.
        Assert.True(store.SetPinned(s_api, s_heap, pinned: true));
        Assert.Equal([new MetricPin(s_api, s_heap)], store.GetPins());
        Assert.True(Directory.Exists(databasePath));
        Assert.Empty(Directory.GetFileSystemEntries(databasePath));
    }

    [Fact]
    public void GetPinnedInstruments_ReturnsReportedInstrumentsInPinnedOrder()
    {
        var store = new MetricPinStore(databasePath: null, NullLogger.Instance);
        store.SetPinned(s_api, s_cpu, pinned: true);
        store.SetPinned(s_worker, s_duration, pinned: true);
        store.SetPinned(s_api, s_heap, pinned: true);

        // The API doesn't report the HTTP duration instrument, so that pin is skipped.
        var heap = CreateInstrument(s_api, s_heap);
        var cpu = CreateInstrument(s_api, s_cpu);
        var unpinned = CreateInstrument(s_api, new OtlpInstrumentKey("System.Runtime", "dotnet.exceptions"));

        Assert.Equal([cpu, heap], store.GetPinnedInstruments([heap, unpinned, cpu]));
    }

    [Fact]
    public void SetPinned_SameInstrumentForResourcesAndReplicas_IsIndependent()
    {
        var store = new MetricPinStore(databasePath: null, NullLogger.Instance);
        var apiReplica = new ResourceKey("api", "first");
        var secondApiReplica = new ResourceKey("api", "second");
        var apiInstrument = CreateInstrument(apiReplica, s_cpu);
        var secondApiInstrument = CreateInstrument(secondApiReplica, s_cpu);
        var workerInstrument = CreateInstrument(s_worker, s_cpu);

        store.SetPinned(apiReplica, s_cpu, pinned: true);
        store.SetPinned(s_worker, s_cpu, pinned: true);

        Assert.True(store.IsPinned(apiReplica, s_cpu));
        Assert.False(store.IsPinned(secondApiReplica, s_cpu));
        Assert.True(store.IsPinned(s_worker, s_cpu));
        Assert.Equal([apiInstrument, workerInstrument], store.GetPinnedInstruments([apiInstrument, secondApiInstrument, workerInstrument]));

        store.SetPinned(apiReplica, s_cpu, pinned: false);

        Assert.False(store.IsPinned(apiReplica, s_cpu));
        Assert.True(store.IsPinned(s_worker, s_cpu));
        Assert.Equal([workerInstrument], store.GetPinnedInstruments([apiInstrument, secondApiInstrument, workerInstrument]));
    }

    [Fact]
    public void ExistingGlobalPins_RemainGlobalUntilExplicitlyUnpinned()
    {
        var databasePath = Path.Combine(_workspace.Path, DashboardSettingsDatabase.DatabaseFileName);
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE metric_pins (
                    pin_id INTEGER NOT NULL PRIMARY KEY,
                    meter_name TEXT NOT NULL,
                    instrument_name TEXT NOT NULL,
                    UNIQUE (meter_name, instrument_name)
                );
                INSERT INTO metric_pins (meter_name, instrument_name) VALUES ($meter, $instrument);
                """;
            command.Parameters.AddWithValue("$meter", s_cpu.MeterName);
            command.Parameters.AddWithValue("$instrument", s_cpu.InstrumentName);
            command.ExecuteNonQuery();
        }

        var store = new MetricPinStore(databasePath, NullLogger.Instance);
        var apiInstrument = CreateInstrument(s_api, s_cpu);
        var workerInstrument = CreateInstrument(s_worker, s_cpu);

        Assert.Equal([new MetricPin(null, s_cpu)], store.GetPins());
        Assert.Equal([apiInstrument, workerInstrument], store.GetPinnedInstruments([apiInstrument, workerInstrument]));
        Assert.True(store.IsPinned(s_api, s_cpu));
        Assert.True(store.IsPinned(s_worker, s_cpu));

        // Removing the migrated pin is an explicit global action. Re-pinning afterwards creates resource scope.
        Assert.True(store.SetPinned(s_api, s_cpu, pinned: false));
        Assert.False(store.IsPinned(s_api, s_cpu));
        Assert.False(store.IsPinned(s_worker, s_cpu));
        Assert.True(store.SetPinned(s_api, s_cpu, pinned: true));

        store = new MetricPinStore(databasePath, NullLogger.Instance);
        Assert.Equal([new MetricPin(s_api, s_cpu)], store.GetPins());
        Assert.True(store.IsPinned(s_api, s_cpu));
        Assert.False(store.IsPinned(s_worker, s_cpu));
    }

    private static OtlpInstrumentSummary CreateInstrument(ResourceKey resourceKey, OtlpInstrumentKey key)
    {
        var context = new OtlpContext { Options = new TelemetryLimitOptions(), Logger = NullLogger.Instance };
        var resource = new OtlpResource(resourceKey.Name, resourceKey.InstanceId, uninstrumentedPeer: false, context);

        return new OtlpInstrumentSummary
        {
            Name = key.InstrumentName,
            Description = string.Empty,
            Unit = string.Empty,
            Type = OtlpInstrumentType.Sum,
            AggregationTemporality = OtlpAggregationTemporality.Cumulative,
            IsMonotonic = true,
            Parent = new OtlpScope(key.MeterName, version: string.Empty, attributes: []),
            ResourceView = new OtlpResourceView(resource, Array.Empty<KeyValuePair<string, string>>())
        };
    }

    public void Dispose() => _workspace.Dispose();
}
