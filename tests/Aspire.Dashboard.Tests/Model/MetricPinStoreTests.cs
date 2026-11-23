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

        store.SetPinned(s_heap, ["api"], pinned: true);
        store.SetPinned(s_duration, ["api"], pinned: true);
        store.SetPinned(s_cpu, ["api"], pinned: true);
        store.SetPinned(s_duration, ["api"], pinned: false);

        Assert.Equal(
            """[{"resourceName":"api","meterName":"System.Runtime","instrumentName":"dotnet.gc.heap.total_allocated"},{"resourceName":"api","meterName":"System.Runtime","instrumentName":"dotnet.process.cpu.time"}]""",
            _browser.Items[s_storageKey]);

        // A new store reads the saved pins, as a new circuit does after a refresh or a restart.
        store = await CreateLoadedStoreAsync();
        Assert.Equal([Pin("api", s_heap), Pin("api", s_cpu)], store.GetPins());
        Assert.True(store.IsPinned(s_heap, ["api"]));
        Assert.False(store.IsPinned(s_duration, ["api"]));

        // Re-pinning moves the instrument to the end because pins are listed in the order they were pinned.
        store.SetPinned(s_duration, ["api"], pinned: true);
        store = await CreateLoadedStoreAsync();
        Assert.Equal([Pin("api", s_heap), Pin("api", s_cpu), Pin("api", s_duration)], store.GetPins());
    }

    [Fact]
    public async Task SetPinned_AppliesToEachOfTheSelectedResources()
    {
        var store = await CreateLoadedStoreAsync();

        // Pinning while two resources are selected pins the instrument for both, and only for them.
        store.SetPinned(s_duration, ["api", "worker"], pinned: true);
        Assert.Equal([Pin("api", s_duration), Pin("worker", s_duration)], store.GetPins());
        Assert.False(store.IsPinned(s_duration, ["cache"]));

        // It's pinned for a selection when any of its resources has it pinned. Names ignore case, as resource names do.
        Assert.True(store.IsPinned(s_duration, ["cache", "WORKER"]));

        // Pinning it again for a resource that has it keeps a single pin.
        store.SetPinned(s_duration, ["api", "cache"], pinned: true);
        Assert.Equal([Pin("api", s_duration), Pin("worker", s_duration), Pin("cache", s_duration)], store.GetPins());

        // Unpinning unpins it for every selected resource, and leaves the others.
        store.SetPinned(s_duration, ["worker", "cache"], pinned: false);
        Assert.Equal([Pin("api", s_duration)], store.GetPins());
    }

    [Fact]
    public async Task SetPinned_BeforeLoadIsMergedWithStoredPins()
    {
        var store = await CreateLoadedStoreAsync();
        store.SetPinned(s_heap, ["api"], pinned: true);

        // Pinning before the stored pins are read must not replace them.
        store = CreateStore();
        store.SetPinned(s_cpu, ["api"], pinned: true);
        await store.EnsureLoadedAsync().DefaultTimeout();

        Assert.Equal([Pin("api", s_heap), Pin("api", s_cpu)], store.GetPins());
        Assert.Equal([Pin("api", s_heap), Pin("api", s_cpu)], (await CreateLoadedStoreAsync()).GetPins());
    }

    [Fact]
    public async Task SetPinned_ChangesFromAnotherTabAreKept()
    {
        var tabA = await CreateLoadedStoreAsync();
        var tabB = await CreateLoadedStoreAsync();
        tabA.SetPinned(s_heap, ["api"], pinned: true);
        tabA.SetPinned(s_duration, ["api"], pinned: true);

        // Tab B didn't see tab A's pins. Its changes are applied to what tab A saved.
        tabB.SetPinned(s_cpu, ["api"], pinned: true);
        tabB.SetPinned(s_duration, ["api"], pinned: false);

        Assert.Equal([Pin("api", s_heap), Pin("api", s_cpu)], tabB.GetPins());
        Assert.Equal([Pin("api", s_heap), Pin("api", s_cpu)], (await CreateLoadedStoreAsync()).GetPins());
    }

    [Fact]
    public void SetPinned_RaisesChangedOnlyWhenPinsChange()
    {
        var store = CreateStore();
        var changedCount = 0;
        store.Changed += () => changedCount++;

        store.SetPinned(s_heap, ["api"], pinned: true);
        store.SetPinned(s_heap, ["api"], pinned: true);
        store.SetPinned(s_cpu, ["api"], pinned: false);
        store.SetPinned(s_heap, ["worker"], pinned: false);
        store.SetPinned(s_heap, ["api"], pinned: false);

        Assert.Equal(2, changedCount);
        Assert.Empty(store.GetPins());
    }

    [Fact]
    public void GetPinnedInstruments_ReturnsInstrumentsPinnedForTheirResourceInPinnedOrder()
    {
        var store = CreateStore();
        store.SetPinned(s_cpu, ["api"], pinned: true);
        store.SetPinned(s_duration, ["worker"], pinned: true);
        store.SetPinned(s_heap, ["api"], pinned: true);

        var apiHeap = CreateInstrument("api", s_heap);
        var apiCpu = CreateInstrument("api", s_cpu);
        var apiDuration = CreateInstrument("api", s_duration);
        var workerCpu = CreateInstrument("worker", s_cpu);
        var workerDuration = CreateInstrument("worker", s_duration);

        // The CPU instrument is pinned for api only, and the duration for worker only.
        Assert.Equal([apiCpu, workerDuration, apiHeap], store.GetPinnedInstruments([apiHeap, workerCpu, apiDuration, apiCpu, workerDuration]));
        Assert.True(store.IsPinned(apiCpu));
        Assert.False(store.IsPinned(workerCpu));
    }

    [Fact]
    public async Task Load_DropsPinsWithoutAResource()
    {
        // Pins saved before they were scoped to resources have no resource name, so they can't be attributed.
        _browser.Items[s_storageKey] =
            """[{"meterName":"System.Runtime","instrumentName":"dotnet.gc.heap.total_allocated"},{"resourceName":"api","meterName":"System.Runtime","instrumentName":"dotnet.process.cpu.time"}]""";

        var store = await CreateLoadedStoreAsync();

        Assert.Equal([Pin("api", s_cpu)], store.GetPins());
    }

    private static MetricPin Pin(string resourceName, OtlpInstrumentKey key) => new(resourceName, key.MeterName, key.InstrumentName);

    private static OtlpInstrumentSummary CreateInstrument(string resourceName, OtlpInstrumentKey key)
    {
        var context = new OtlpContext { Options = new TelemetryLimitOptions(), Logger = NullLogger.Instance };
        var resource = new OtlpResource(resourceName, instanceId: null, uninstrumentedPeer: false, context);

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
