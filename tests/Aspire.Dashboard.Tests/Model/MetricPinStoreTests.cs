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
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public sealed class MetricPinStoreTests
{
    private static readonly OtlpInstrumentKey s_duration = new("Microsoft.AspNetCore.Hosting", "http.server.request.duration");
    private static readonly OtlpInstrumentKey s_heap = new("System.Runtime", "dotnet.gc.heap.total_allocated");
    private static readonly OtlpInstrumentKey s_cpu = new("System.Runtime", "dotnet.process.cpu.time");

    private static readonly string s_storageKey = BrowserStorageKeys.MetricPinsKey("TestApp");

    private readonly InMemoryBrowserLocalStorage _browser = new();
    private readonly LocalStorageLocks _locks = new();

    [Fact]
    public async Task SetPinned_PersistsInPinnedOrderAcrossStores()
    {
        var store = await CreateLoadedStoreAsync();

        await store.SetPinnedAsync(s_heap, ["api"], pinned: true);
        await store.SetPinnedAsync(s_duration, ["api"], pinned: true);
        await store.SetPinnedAsync(s_cpu, ["api"], pinned: true);
        await store.SetPinnedAsync(s_duration, ["api"], pinned: false);

        Assert.True((await _browser.Storage.GetUnprotectedAsync<List<MetricPin>>(s_storageKey)).InvalidValue);

        // A new store reads the saved pins, as a new circuit does after a refresh or a restart.
        store = await CreateLoadedStoreAsync();
        Assert.Equal([Pin("api", s_heap), Pin("api", s_cpu)], store.GetPins());
        Assert.True(store.IsPinned(s_heap, ["api"]));
        Assert.False(store.IsPinned(s_duration, ["api"]));

        // Re-pinning moves the instrument to the end because pins are listed in the order they were pinned.
        await store.SetPinnedAsync(s_duration, ["api"], pinned: true);
        store = await CreateLoadedStoreAsync();
        Assert.Equal([Pin("api", s_heap), Pin("api", s_cpu), Pin("api", s_duration)], store.GetPins());
    }

    [Fact]
    public async Task SetPinned_AppliesToEachOfTheSelectedResources()
    {
        var store = await CreateLoadedStoreAsync();

        // Pinning while two resources are selected pins the instrument for both, and only for them.
        await store.SetPinnedAsync(s_duration, ["api", "worker"], pinned: true);
        Assert.Equal([Pin("api", s_duration), Pin("worker", s_duration)], store.GetPins());
        Assert.False(store.IsPinned(s_duration, ["cache"]));

        // It's pinned for a selection when any of its resources has it pinned. Names ignore case, as resource names do.
        Assert.True(store.IsPinned(s_duration, ["cache", "WORKER"]));

        // Pinning it again for a resource that has it keeps a single pin.
        await store.SetPinnedAsync(s_duration, ["api", "cache"], pinned: true);
        Assert.Equal([Pin("api", s_duration), Pin("worker", s_duration), Pin("cache", s_duration)], store.GetPins());

        // Unpinning unpins it for every selected resource, and leaves the others.
        await store.SetPinnedAsync(s_duration, ["worker", "cache"], pinned: false);
        Assert.Equal([Pin("api", s_duration)], store.GetPins());
    }

    [Fact]
    public async Task SetPinned_BeforeLoadIsMergedWithStoredPins()
    {
        var store = await CreateLoadedStoreAsync();
        await store.SetPinnedAsync(s_heap, ["api"], pinned: true);

        // Pinning before the stored pins are read must not replace them.
        store = CreateStore();
        await store.SetPinnedAsync(s_cpu, ["api"], pinned: true);
        await store.EnsureLoadedAsync().DefaultTimeout();

        Assert.Equal([Pin("api", s_heap), Pin("api", s_cpu)], store.GetPins());
        Assert.Equal([Pin("api", s_heap), Pin("api", s_cpu)], (await CreateLoadedStoreAsync()).GetPins());
    }

    [Fact]
    public async Task SetPinned_ChangesFromAnotherTabAreKept()
    {
        var tabA = await CreateLoadedStoreAsync();
        var tabB = await CreateLoadedStoreAsync();
        await tabA.SetPinnedAsync(s_heap, ["api"], pinned: true);
        await tabA.SetPinnedAsync(s_duration, ["api"], pinned: true);

        // Tab B didn't see tab A's pins. Its changes are applied to what tab A saved.
        await tabB.SetPinnedAsync(s_cpu, ["api"], pinned: true);
        await tabB.SetPinnedAsync(s_duration, ["api"], pinned: false);

        Assert.Equal([Pin("api", s_heap), Pin("api", s_cpu)], tabB.GetPins());
        Assert.Equal([Pin("api", s_heap), Pin("api", s_cpu)], (await CreateLoadedStoreAsync()).GetPins());
    }

    [Fact]
    public async Task SetPinned_RejectsASelectionThatWouldExceedFiftyPinsEvenAcrossTabs()
    {
        var firstTab = await CreateLoadedStoreAsync();
        var staleTab = await CreateLoadedStoreAsync();
        var names = Enumerable.Range(1, 49).Select(i => FormattableString.Invariant($"resource-{i:D2}")).ToArray();
        var initialPins = names.Select(name => Pin(name, s_heap)).ToArray();
        await firstTab.SetPinnedAsync(s_heap, names, pinned: true);

        Assert.Equal(SettingUpdateResult.LimitReached, await firstTab.SetPinnedAsync(s_cpu, ["api", "worker"], pinned: true));
        Assert.Equal(initialPins, firstTab.GetPins());

        // A rejected edit refreshes the stale view but doesn't publish the partially updated selection.
        var storedPins = _browser.Items[s_storageKey];
        var changes = 0;
        staleTab.Changed += () => changes++;
        Assert.Equal(SettingUpdateResult.LimitReached, await staleTab.SetPinnedAsync(s_cpu, ["api", "worker"], pinned: true));
        Assert.Equal(initialPins, staleTab.GetPins());
        Assert.Equal(1, changes);
        Assert.Equal(storedPins, _browser.Items[s_storageKey]);
        Assert.Equal(initialPins, (await CreateLoadedStoreAsync()).GetPins());

        await staleTab.SetPinnedAsync(s_cpu, ["api"], pinned: true);
        await staleTab.SetPinnedAsync(s_cpu, ["worker"], pinned: true);
        Assert.Equal([.. initialPins, Pin("api", s_cpu)], (await CreateLoadedStoreAsync()).GetPins());

        await staleTab.SetPinnedAsync(s_cpu, ["api"], pinned: false);
        await staleTab.SetPinnedAsync(s_cpu, ["worker"], pinned: true);
        Assert.Equal([.. initialPins, Pin("worker", s_cpu)], (await CreateLoadedStoreAsync()).GetPins());
    }

    [Fact]
    public async Task SetPinned_RaisesChangedOnlyWhenPinsChange()
    {
        var store = CreateStore();
        var changedCount = 0;
        store.Changed += () => changedCount++;

        await store.SetPinnedAsync(s_heap, ["api"], pinned: true);
        await store.SetPinnedAsync(s_heap, ["api"], pinned: true);
        await store.SetPinnedAsync(s_cpu, ["api"], pinned: false);
        await store.SetPinnedAsync(s_heap, ["worker"], pinned: false);
        await store.SetPinnedAsync(s_heap, ["api"], pinned: false);

        Assert.Equal(2, changedCount);
        Assert.Empty(store.GetPins());
    }

    [Fact]
    public async Task GetPinnedInstruments_ReturnsInstrumentsPinnedForTheirResourceInPinnedOrder()
    {
        var store = CreateStore();
        await store.SetPinnedAsync(s_cpu, ["api"], pinned: true);
        await store.SetPinnedAsync(s_duration, ["worker"], pinned: true);
        await store.SetPinnedAsync(s_heap, ["api"], pinned: true);

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
    public async Task Load_IgnoresPlaintextPinsUntilTheNextChange()
    {
        await _browser.Storage.SetUnprotectedAsync<List<MetricPin>>(s_storageKey, [Pin("api", s_heap)]);
        var plaintext = _browser.Items[s_storageKey];
        var store = await CreateLoadedStoreAsync();
        Assert.Empty(store.GetPins());
        Assert.Equal(plaintext, _browser.Items[s_storageKey]);

        await store.SetPinnedAsync(s_cpu, ["api"], pinned: true);

        Assert.Equal([Pin("api", s_cpu)], (await CreateLoadedStoreAsync()).GetPins());
        Assert.Equal([s_storageKey], _browser.Items.Keys);
    }

    [Fact]
    public async Task Load_DropsPinsWithoutAResource()
    {
        await _browser.Storage.SetAsync<List<MetricPin>>(s_storageKey, [Pin("", s_heap), Pin("api", s_cpu)]);
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
        new(_browser.Storage, _locks, Options.Create(new DashboardOptions { ApplicationName = "TestApp" }), NullLogger<MetricPinStore>.Instance);

    private async Task<MetricPinStore> CreateLoadedStoreAsync()
    {
        var store = CreateStore();
        await store.EnsureLoadedAsync().DefaultTimeout();
        return store;
    }
}
