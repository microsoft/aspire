// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.BrowserStorage;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Dashboard.Utils;
using Aspire.Tests.Shared.DashboardModel;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public sealed class ResourceOrderStoreTests
{
    private static readonly string s_storageKey = BrowserStorageKeys.ResourceOrderKey("TestApp");

    private readonly InMemoryBrowserLocalStorage _browser = new();
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly LocalStorageLocks _locks = new();

    [Fact]
    public async Task AppHostDefaultsSeedOrderingAndSavedMovesTakePrecedence()
    {
        var store = await CreateLoadedStoreAsync();
        var name = KnownProperties.Resource.TraitPrefix + KnownResourceTraits.DashboardPosition;
        var api = ModelTestHelpers.CreateResource("api", properties: new()
        {
            [name] = new(name, Value.ForList(Value.ForString("20")), false, null, 0, null, false)
        });
        var cache = ModelTestHelpers.CreateResource("cache", properties: new()
        {
            [name] = new(name, Value.ForList(Value.ForString("-10")), false, null, 0, null, false)
        });
        var worker = ModelTestHelpers.CreateResource("worker");
        store.EnsureResources([api, cache, worker]);

        Assert.Equal([cache.PersistentKey, api.PersistentKey, worker.PersistentKey],
            store.GetPositions().OrderBy(p => p.Value).Select(p => p.Key));

        // A restart must not turn inferred order into a user customization.
        store = await CreateLoadedStoreAsync();
        cache.Properties[name].Value.ListValue.Values[0].StringValue = "30";
        store.EnsureResources([api, cache, worker]);
        Assert.Equal([api.PersistentKey, cache.PersistentKey, worker.PersistentKey],
            store.GetPositions().OrderBy(p => p.Value).Select(p => p.Key));
        Assert.True(store.TryMove(cache.PersistentKey, api.PersistentKey, false));

        store = await CreateLoadedStoreAsync();
        store.EnsureResources([api, cache, worker]);
        Assert.Equal([cache.PersistentKey, api.PersistentKey, worker.PersistentKey],
            store.GetPositions().OrderBy(p => p.Value).Select(p => p.Key));
    }

    [Fact]
    public async Task GroupOrder_PersistsIndependentScopes()
    {
        var store = await CreateLoadedStoreAsync();
        store.EnsureResources(["cache_0", "api_0"]);
        store.EnsureGroups("Tags", ["Container", "Untagged", ""]);
        store.EnsureGroups("ResourceType", ["Container", "Project"]);
        store.EnsureGroups("State", ["Running", "Waiting"]);
        Assert.True(store.TryMoveGroup("Tags", "", "Container", after: false));
        Assert.True(store.TryMoveGroup("ResourceType", "Project", "Container", after: false));
        Assert.True(store.TryMoveGroup("State", "Waiting", "Running", after: false));

        Assert.Equal(
            """{"resources":["cache_0","api_0"],"groups":{"Tags":["","Container","Untagged"],"ResourceType":["Project","Container"],"State":["Waiting","Running"]},"customOrder":false}""",
            _browser.Items[s_storageKey]);

        var reloaded = await CreateLoadedStoreAsync();
        reloaded.EnsureGroups("Tags", ["Container", "A new group"]);
        Assert.Equal(["", "Container", "Untagged", "A new group"], GetOrderedGroupKeys(reloaded, "Tags"));
        Assert.Equal(["Project", "Container"], GetOrderedGroupKeys(reloaded, "ResourceType"));
        Assert.Equal(["Waiting", "Running"], GetOrderedGroupKeys(reloaded, "State"));
        Assert.Equal(["cache_0", "api_0"], GetOrderedKeys(reloaded));
        Assert.Equal(["", "Container", "Untagged", "A new group"], GetOrderedGroupKeys(await CreateLoadedStoreAsync(), "Tags"));
    }

    [Fact]
    public void GroupOrder_ConcurrentUpdatesKeepUniquePositionsInEachScope()
    {
        var store = CreateStore();
        Parallel.For(0, 100, i =>
        {
            store.EnsureGroups("Tags", [$"group_{i}", "shared"]);
            store.EnsureGroups("State", [$"group_{i}"]);
        });

        Assert.Equal(Enumerable.Range(0, 101).Select(i => (long)i), store.GetGroupPositions("Tags").Values.Order());
        Assert.Equal(Enumerable.Range(0, 100).Select(i => (long)i), store.GetGroupPositions("State").Values.Order());
        Assert.Empty(store.GetPositions());
    }

    [Fact]
    public async Task Order_PersistsAndAppendsNewResources()
    {
        var store = await CreateLoadedStoreAsync();
        store.EnsureResources(["api_0", "worker_0", "api_1"]);
        Assert.True(store.TryMove("api_1", "worker_0", after: false));

        var reloaded = await CreateLoadedStoreAsync();
        reloaded.EnsureResources(["worker_0", "new_0", "api_0"]);
        Assert.Equal(["api_0", "api_1", "worker_0", "new_0"], GetOrderedKeys(reloaded));
        Assert.Equal(["api_0", "api_1", "worker_0", "new_0"], GetOrderedKeys(await CreateLoadedStoreAsync()));
    }

    [Fact]
    public async Task Order_PositionsAssignedBeforeLoadFollowStoredPositions()
    {
        var store = await CreateLoadedStoreAsync();
        store.EnsureResources(["api_0", "worker_0"]);
        Assert.True(store.TryMove("worker_0", "api_0", after: false));
        store.EnsureGroups("State", ["Running"]);

        // Positions assigned before the stored order is read must not replace it.
        var reloaded = CreateStore();
        reloaded.EnsureResources(["new_0", "api_0"]);
        reloaded.EnsureGroups("State", ["Waiting"]);
        await reloaded.EnsureLoadedAsync().DefaultTimeout();

        Assert.Equal(["worker_0", "api_0", "new_0"], GetOrderedKeys(reloaded));
        Assert.Equal(["Running", "Waiting"], GetOrderedGroupKeys(reloaded, "State"));
        Assert.Equal("""{"resources":["worker_0","api_0","new_0"],"groups":{"State":["Running","Waiting"]},"customOrder":true}""", _browser.Items[s_storageKey]);
    }

    [Fact]
    public async Task Order_ChangesFromAnotherTabAreKept()
    {
        var tabA = await CreateLoadedStoreAsync();
        tabA.EnsureResources(["api_0", "cache_0", "worker_0"]);
        var tabB = await CreateLoadedStoreAsync();
        Assert.True(tabA.TryMove("worker_0", "api_0", after: false));

        // Tab B didn't see tab A's move. A new resource in tab B is added after the order tab A saved.
        tabB.EnsureResources(["new_0"]);

        Assert.Equal(["worker_0", "api_0", "cache_0", "new_0"], GetOrderedKeys(tabB));
        Assert.Equal("""{"resources":["worker_0","api_0","cache_0","new_0"],"groups":{},"customOrder":true}""", _browser.Items[s_storageKey]);
    }

    [Fact]
    public async Task Order_UnreadableStorageIsNotOverwritten()
    {
        var store = await CreateLoadedStoreAsync();
        store.EnsureResources(["api_0", "worker_0"]);
        var storedValue = _browser.Items[s_storageKey];

        _browser.FailReads = true;
        var reloaded = await CreateLoadedStoreAsync();
        reloaded.EnsureResources(["new_0"]);

        Assert.Equal(["new_0"], GetOrderedKeys(reloaded));
        Assert.Equal(storedValue, _browser.Items[s_storageKey]);
    }

    [Fact]
    public async Task Order_InvalidStoredValueIsKeptAndReplaced()
    {
        _browser.Items[s_storageKey] = "[1, 2]";
        var store = await CreateLoadedStoreAsync();
        store.EnsureResources(["api_0"]);

        Assert.Equal(["api_0"], GetOrderedKeys(store));
        Assert.Equal("""{"resources":["api_0"],"groups":{},"customOrder":false}""", _browser.Items[s_storageKey]);
        Assert.Equal("[1, 2]", _browser.Items[$"{s_storageKey}_Unreadable"]);
    }

    [Fact]
    public void Order_SupportsMovingBeforeAndAfter()
    {
        var store = CreateStore();
        var changes = 0;
        store.Changed += () => changes++;
        store.EnsureResources(["api_0", "cache_0", "worker_0"]);
        Assert.True(store.TryMove("worker_0", "api_0", after: false));
        Assert.Equal(["worker_0", "api_0", "cache_0"], GetOrderedKeys(store));
        Assert.True(store.TryMove("WORKER_0", "CACHE_0", after: true));
        Assert.True(store.TryMove("api_0", "API_0", after: false));
        Assert.Equal(["api_0", "cache_0", "WORKER_0"], GetOrderedKeys(store));
        Assert.Equal(3, changes);
    }

    [Fact]
    public void EnsureResources_RetainsAbsentResourcesAndDoesNotDuplicateKeys()
    {
        var store = CreateStore();
        store.EnsureResources(["API_0", "worker_0", "worker_1"]);
        store.EnsureResources(["WORKER_0", "new_0", "NEW_0"]);
        Assert.Equal(["API_0", "worker_0", "worker_1", "new_0"], GetOrderedKeys(store));
    }

    [Fact]
    public void EnsureResources_ConcurrentUpdatesHaveUniquePositions()
    {
        var store = CreateStore();
        Parallel.For(0, 100, i => store.EnsureResources([$"resource_{i}", "shared_0"]));

        var positions = store.GetPositions();
        Assert.Equal(101, positions.Count);
        Assert.Equal(Enumerable.Range(0, 101).Select(i => (long)i), positions.Values.Order());
        Assert.All(Enumerable.Range(0, 100), i => Assert.True(positions.ContainsKey($"resource_{i}")));
    }

    [Fact]
    public void TryMove_UnknownKeyIsRejected()
    {
        var store = CreateStore();
        store.EnsureResources(["api_0"]);

        Assert.False(store.TryMove("missing_0", "api_0", after: false));
        Assert.Equal(["api_0"], GetOrderedKeys(store));
    }

    private static string[] GetOrderedKeys(ResourceOrderStore store) =>
        store.GetPositions().OrderBy(pair => pair.Value).Select(pair => pair.Key).ToArray();

    private static string[] GetOrderedGroupKeys(ResourceOrderStore store, string grouping) =>
        store.GetGroupPositions(grouping).OrderBy(pair => pair.Value).Select(pair => pair.Key).ToArray();

    private ResourceOrderStore CreateStore() =>
        new(_browser.Storage, _locks, _timeProvider, Options.Create(new DashboardOptions { ApplicationName = "TestApp" }), NullLogger<ResourceOrderStore>.Instance);

    private async Task<ResourceOrderStore> CreateLoadedStoreAsync()
    {
        var store = CreateStore();
        await store.EnsureLoadedAsync().DefaultTimeout();
        return store;
    }
}
