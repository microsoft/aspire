// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.BrowserStorage;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public sealed class MetricPinStoreTests
{
    private static readonly OtlpInstrumentKey s_duration = new("Microsoft.AspNetCore.Hosting", "http.server.request.duration");
    private static readonly OtlpInstrumentKey s_heap = new("System.Runtime", "dotnet.gc.heap.total_allocated");
    private static readonly OtlpInstrumentKey s_cpu = new("System.Runtime", "dotnet.process.cpu.time");

    private static readonly string s_storageKey = BrowserStorageKeys.MetricPinsKey("TestApp");

    private readonly InMemoryBrowserLocalStorage _browser = new();
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly LocalStorageLocks _locks = new();

    [Fact]
    public async Task SetPinned_PersistsInPinnedOrderAcrossStores()
    {
        var store = await CreateLoadedStoreAsync();

        store.SetPinned(s_heap, pinned: true);
        store.SetPinned(s_duration, pinned: true);
        store.SetPinned(s_cpu, pinned: true);
        store.SetPinned(s_duration, pinned: false);

        Assert.Equal(
            """[{"meterName":"System.Runtime","instrumentName":"dotnet.gc.heap.total_allocated"},{"meterName":"System.Runtime","instrumentName":"dotnet.process.cpu.time"}]""",
            _browser.Items[s_storageKey]);

        // A new store reads the saved pins, as a new circuit does after a refresh or a restart.
        store = await CreateLoadedStoreAsync();
        Assert.Equal([s_heap, s_cpu], store.GetPins());
        Assert.True(store.IsPinned(s_heap));
        Assert.False(store.IsPinned(s_duration));

        // Re-pinning moves the instrument to the end because pins are listed in the order they were pinned.
        store.SetPinned(s_duration, pinned: true);
        store = await CreateLoadedStoreAsync();
        Assert.Equal([s_heap, s_cpu, s_duration], store.GetPins());
    }

    [Fact]
    public async Task SetPinned_BeforeLoadIsMergedWithStoredPins()
    {
        var store = await CreateLoadedStoreAsync();
        store.SetPinned(s_heap, pinned: true);

        // Pinning before the stored pins are read must not replace them.
        store = CreateStore();
        store.SetPinned(s_cpu, pinned: true);
        await store.EnsureLoadedAsync().DefaultTimeout();

        Assert.Equal([s_heap, s_cpu], store.GetPins());
        Assert.Equal([s_heap, s_cpu], (await CreateLoadedStoreAsync()).GetPins());
    }

    [Fact]
    public async Task SetPinned_ChangesFromAnotherTabAreKept()
    {
        var tabA = await CreateLoadedStoreAsync();
        var tabB = await CreateLoadedStoreAsync();
        tabA.SetPinned(s_heap, pinned: true);
        tabA.SetPinned(s_duration, pinned: true);

        // Tab B didn't see tab A's pins. Its changes are applied to what tab A saved.
        tabB.SetPinned(s_cpu, pinned: true);
        tabB.SetPinned(s_duration, pinned: false);

        Assert.Equal([s_heap, s_cpu], tabB.GetPins());
        Assert.Equal([s_heap, s_cpu], (await CreateLoadedStoreAsync()).GetPins());
    }

    [Fact]
    public void SetPinned_RaisesChangedOnlyWhenPinsChange()
    {
        var store = CreateStore();
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
    public void GetPinnedInstruments_ReturnsReportedInstrumentsInPinnedOrder()
    {
        var store = CreateStore();
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

    private MetricPinStore CreateStore() =>
        new(_browser.Storage, _locks, _timeProvider, Options.Create(new DashboardOptions { ApplicationName = "TestApp" }), NullLogger<MetricPinStore>.Instance);

    private async Task<MetricPinStore> CreateLoadedStoreAsync()
    {
        var store = CreateStore();
        await store.EnsureLoadedAsync().DefaultTimeout();
        return store;
    }
}
