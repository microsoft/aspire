// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public sealed class MetricPinStoreTests(ITestOutputHelper testOutputHelper) : IDisposable
{
    private static readonly OtlpInstrumentKey s_duration = new("Microsoft.AspNetCore.Hosting", "http.server.request.duration");
    private static readonly OtlpInstrumentKey s_heap = new("System.Runtime", "dotnet.gc.heap.total_allocated");
    private static readonly OtlpInstrumentKey s_cpu = new("System.Runtime", "dotnet.process.cpu.time");

    private readonly TemporaryWorkspace _workspace = TemporaryWorkspace.Create(testOutputHelper);

    [Fact]
    public void SetPinned_PersistsInPinnedOrderAcrossInstances()
    {
        var databasePath = Path.Combine(_workspace.Path, DashboardSettingsDatabase.DatabaseFileName);
        var store = new MetricPinStore(databasePath, NullLogger.Instance);

        Assert.True(store.SetPinned(s_heap, pinned: true));
        Assert.True(store.SetPinned(s_duration, pinned: true));
        Assert.True(store.SetPinned(s_cpu, pinned: true));
        Assert.True(store.SetPinned(s_duration, pinned: false));

        // A new instance reads the saved pins, as after restarting the dashboard.
        store = new MetricPinStore(databasePath, NullLogger.Instance);
        Assert.Equal([s_heap, s_cpu], store.GetPins());
        Assert.True(store.IsPinned(s_heap));
        Assert.False(store.IsPinned(s_duration));

        // Re-pinning moves the instrument to the end because pins are listed in the order they were pinned.
        Assert.True(store.SetPinned(s_duration, pinned: true));
        store = new MetricPinStore(databasePath, NullLogger.Instance);
        Assert.Equal([s_heap, s_cpu, s_duration], store.GetPins());
    }

    [Fact]
    public void SetPinned_RaisesChangedOnlyWhenPinsChange()
    {
        var store = new MetricPinStore(databasePath: null, NullLogger.Instance);
        var changedCount = 0;
        store.Changed += () => changedCount++;

        store.SetPinned(s_heap, pinned: true);
        store.SetPinned(s_heap, pinned: true);
        store.SetPinned(s_cpu, pinned: false);
        store.SetPinned(s_heap, pinned: false);

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
        Assert.True(store.SetPinned(s_heap, pinned: true));
        Assert.Equal([s_heap], store.GetPins());
        Assert.True(Directory.Exists(databasePath));
        Assert.Empty(Directory.GetFileSystemEntries(databasePath));
    }

    [Fact]
    public void GetPinnedInstruments_ReturnsReportedInstrumentsInPinnedOrder()
    {
        var store = new MetricPinStore(databasePath: null, NullLogger.Instance);
        store.SetPinned(s_cpu, pinned: true);
        store.SetPinned(s_duration, pinned: true);
        store.SetPinned(s_heap, pinned: true);

        // This resource doesn't report the HTTP duration instrument, so that pin is skipped.
        var heap = CreateInstrument(s_heap);
        var cpu = CreateInstrument(s_cpu);
        var unpinned = CreateInstrument(new OtlpInstrumentKey("System.Runtime", "dotnet.exceptions"));

        Assert.Equal([cpu, heap], store.GetPinnedInstruments([heap, unpinned, cpu]));
    }

    private static OtlpInstrumentSummary CreateInstrument(OtlpInstrumentKey key)
    {
        var context = new OtlpContext { Options = new TelemetryLimitOptions(), Logger = NullLogger.Instance };
        var resource = new OtlpResource("resource", instanceId: null, uninstrumentedPeer: false, context);

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
