// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
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
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public sealed class ResourceDisplayStoreTests
{
    private static readonly string s_resourceKey = BrowserStorageKeys.ResourceOrderKey("TestApp");
    private static readonly string s_groupKey = BrowserStorageKeys.GroupOrderKey("TestApp");

    private readonly InMemoryBrowserLocalStorage _browser = new();
    private readonly LocalStorageLocks _locks = new();

    [Fact]
    public async Task DefaultOrderUsesDisplayPriorityWithoutWritingToStorage()
    {
        var store = await CreateLoadedStoreAsync();
        ResourceViewModel[] resources =
        [
            CreateResource("api", "Project", order: 0),
            CreateResource("worker", "Project", order: -10),
            CreateResource("redis", "Container"),
            CreateResource("db", "Container", order: 10),
            CreateResource("admin", "Project", order: 10)
        ];

        Assert.Equal(["admin", "db", "redis", "api", "worker"], GetNames(store, resources));
        Assert.Equal(["Container", "Project"], OrderGroups(store, "ResourceType", "Container", "Project"));
        Assert.Empty(_browser.Items);
    }

    [Fact]
    public async Task ResourceOrder_PersistsOnlyMovesAndSortsNewResourcesByDefault()
    {
        var store = await CreateLoadedStoreAsync();
        var api = CreateResource("api");
        var cache = CreateResource("cache");
        var worker = CreateResource("worker");
        Assert.Equal(ResourceOrderMoveResult.Moved, await store.MoveResourceAsync(worker, cache, after: false, [api, cache, worker]));

        var saved = Assert.Single((await _browser.Storage.GetAsync<List<RelativeOrderMove>>(s_resourceKey)).Value!);
        Assert.Equal(("worker_0", "api_0", "cache_0"), (saved.Key, saved.PreviousKey, saved.NextKey));
        Assert.True((await _browser.Storage.GetUnprotectedAsync<List<RelativeOrderMove>>(s_resourceKey)).InvalidValue);
        Assert.Equal([s_resourceKey], _browser.Items.Keys);

        var reloaded = await CreateLoadedStoreAsync();
        Assert.Equal(["api", "auth", "worker", "cache"], GetNames(reloaded, api, cache, worker, CreateResource("auth")));
        Assert.Equal(["api", "worker"], GetNames(reloaded, api, worker));
        Assert.Equal(["api", "worker", "cache"], GetNames(reloaded, api, cache, worker));
    }

    [Theory]
    [InlineData(true, true, "api,auth,worker,cache")]
    [InlineData(true, false, "api,worker,auth")]
    [InlineData(false, true, "auth,worker,cache")]
    [InlineData(false, false, "auth,worker")]
    public async Task ResourceOrder_UsesSurvivingNeighbors(bool hasPrevious, bool hasNext, string expected)
    {
        var store = await CreateLoadedStoreAsync();
        var api = CreateResource("api");
        var cache = CreateResource("cache");
        var worker = CreateResource("worker");
        Assert.Equal(ResourceOrderMoveResult.Moved, await store.MoveResourceAsync(worker, cache, after: false, [api, cache, worker]));

        var resources = new List<ResourceViewModel> { worker, CreateResource("auth") };
        if (hasPrevious)
        {
            resources.Add(api);
        }
        if (hasNext)
        {
            resources.Add(cache);
        }

        Assert.Equal(expected.Split(','), GetNames(await CreateLoadedStoreAsync(), resources.ToArray()));
    }

    [Fact]
    public async Task ResourceOrder_SkipsMovesWhenResourcesAreNoLongerSiblings()
    {
        var store = await CreateLoadedStoreAsync();
        var root = CreateResource("root");
        var other = CreateResource("other");
        var api = CreateResource("api", parent: "root");
        var worker = CreateResource("worker", parent: "root");
        Assert.Equal(ResourceOrderMoveResult.Moved, await store.MoveResourceAsync(worker, api, after: false, [root, other, api, worker]));

        Assert.Equal(["worker", "api", "other", "root"], GetNames(store, root, other, api, worker));
        Assert.Equal(["api", "other", "root", "worker"], GetNames(store, root, other, api, CreateResource("worker", parent: "other")));
        Assert.Equal(["worker", "api", "other", "root"], GetNames(store, root, other, api, worker));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResourceOrder_RejectsAmbiguousReplicaMoves(bool differentParents)
    {
        var store = await CreateLoadedStoreAsync();
        var root = CreateResource("root");
        var other = CreateResource("other");
        var api = CreateResource("api-old", parent: differentParents ? other.Name : root.Name, displayName: "api");
        var replacement = CreateResource("api-new", parent: root.Name, displayName: "api");
        var worker = CreateResource("worker", parent: root.Name);
        ResourceViewModel[] resources = [root, other, api, replacement, worker];
        var originalOrder = GetNames(store, resources);

        Assert.Equal(ResourceOrderMoveResult.Invalid, await store.MoveResourceAsync(replacement, worker, after: true, resources));
        Assert.Equal(ResourceOrderMoveResult.Invalid, await store.MoveResourceAsync(worker, replacement, after: false, resources));
        Assert.Empty(_browser.Items);
        Assert.Equal(originalOrder, GetNames(store, resources));

        Assert.Equal(ResourceOrderMoveResult.Moved, await store.MoveResourceAsync(worker, replacement, after: false, [root, other, replacement, worker]));
        Assert.Equal(["worker", "api-new", "other", "root"], GetNames(await CreateLoadedStoreAsync(), root, other, replacement, worker));
    }

    [Fact]
    public async Task ResourceOrder_MergesLatestMovesFromDifferentTabs()
    {
        var tabA = await CreateLoadedStoreAsync();
        var tabB = await CreateLoadedStoreAsync();
        var a = CreateResource("a");
        var b = CreateResource("b");
        var c = CreateResource("c");
        var d = CreateResource("d");

        ResourceViewModel[] resources = [a, b, c, d];
        Assert.Equal(ResourceOrderMoveResult.Moved, await tabA.MoveResourceAsync(a, c, after: true, resources));
        Assert.Equal(ResourceOrderMoveResult.Moved, await tabB.MoveResourceAsync(b, d, after: true, resources));
        Assert.Equal(["c", "a", "d", "b"], GetNames(await CreateLoadedStoreAsync(), resources));

        Assert.Equal(ResourceOrderMoveResult.Moved, await tabA.MoveResourceAsync(a, d, after: true, resources));

        var saved = (await _browser.Storage.GetAsync<List<RelativeOrderMove>>(s_resourceKey)).Value!;
        Assert.Equal(["b_0", "a_0"], saved.Select(move => move.Key));
        Assert.Equal(["c", "d", "a", "b"], GetNames(await CreateLoadedStoreAsync(), resources));
    }

    [Fact]
    public async Task Order_RepeatedMovesReplaceThePreviousMove()
    {
        var store = await CreateLoadedStoreAsync();
        var api = CreateResource("api");
        var worker = CreateResource("worker");

        for (var i = 0; i < 401; i++)
        {
            var after = i % 2 != 0;
            Assert.Equal(ResourceOrderMoveResult.Moved, await store.MoveResourceAsync(worker, api, after, [api, worker]));
            Assert.Equal(ResourceOrderMoveResult.Moved, await store.MoveGroupAsync("Tags", "Worker", "Api", after, OrderGroups(store, "Tags", "Api", "Worker")));
        }

        var savedResources = (await _browser.Storage.GetAsync<List<RelativeOrderMove>>(s_resourceKey)).Value!;
        var savedGroups = (await _browser.Storage.GetAsync<Dictionary<string, List<RelativeOrderMove>>>(s_groupKey)).Value!;
        Assert.Equal("api_0", Assert.Single(savedResources).NextKey);
        Assert.Equal("Api", Assert.Single(savedGroups["Tags"]).NextKey);

        var reloaded = await CreateLoadedStoreAsync();
        Assert.Equal(["worker", "api"], GetNames(reloaded, api, worker));
        Assert.Equal(["Worker", "Api"], OrderGroups(reloaded, "Tags", "Api", "Worker"));
    }

    [Fact]
    public async Task Order_MovingAnAnchorAgainDoesNotReshuffleOtherItems()
    {
        var store = await CreateLoadedStoreAsync();
        var a = CreateResource("a");
        var b = CreateResource("b");
        var c = CreateResource("c");
        ResourceViewModel[] resources = [a, b, c];
        string[] groups = ["a", "b", "c"];

        Assert.Equal(ResourceOrderMoveResult.Moved, await store.MoveResourceAsync(b, a, after: false, resources));
        Assert.Equal(ResourceOrderMoveResult.Moved, await store.MoveGroupAsync("Tags", "b", "a", after: false, groups));
        Assert.Equal(ResourceOrderMoveResult.Moved, await store.MoveResourceAsync(c, b, after: true, resources));
        Assert.Equal(ResourceOrderMoveResult.Moved, await store.MoveGroupAsync("Tags", "c", "b", after: true, OrderGroups(store, "Tags", groups)));
        Assert.Equal(["b", "c", "a"], GetNames(store, resources));
        Assert.Equal(["b", "c", "a"], OrderGroups(store, "Tags", groups));

        Assert.Equal(ResourceOrderMoveResult.Moved, await store.MoveResourceAsync(b, a, after: false, resources));
        Assert.Equal(ResourceOrderMoveResult.Moved, await store.MoveGroupAsync("Tags", "b", "a", after: false, OrderGroups(store, "Tags", groups)));

        Assert.Equal(["c", "b", "a"], GetNames(store, resources));
        Assert.Equal(["c", "b", "a"], OrderGroups(store, "Tags", groups));
        var reloaded = await CreateLoadedStoreAsync();
        Assert.Equal(["c", "b", "a"], GetNames(reloaded, resources));
        Assert.Equal(["c", "b", "a"], OrderGroups(reloaded, "Tags", groups));
    }

    [Fact]
    public async Task Order_EvictsOldMovesAndKeepsProtectedPayloadUnderMessageLimit()
    {
        var store = await CreateLoadedStoreAsync();
        var prefix = new string('a', 61); // The three-digit suffix makes each name 64 characters.
        var previousAnchor = CreateResource($"{prefix}000");
        var nextAnchor = CreateResource($"{prefix}001");
        var names = Enumerable.Range(2, 51).Select(i => prefix + i.ToString("D3", CultureInfo.InvariantCulture)).ToArray();

        for (var i = 0; i < names.Length; i++)
        {
            var resource = CreateResource(names[i]);
            Assert.Equal(ResourceOrderMoveResult.Moved, await store.MoveResourceAsync(resource, nextAnchor, after: false, [previousAnchor, nextAnchor, resource]));
            var grouping = i % 2 == 0 ? "Tags" : "ResourceType";
            Assert.Equal(ResourceOrderMoveResult.Moved, await store.MoveGroupAsync(grouping, names[i], nextAnchor.DisplayName, after: false,
                [previousAnchor.DisplayName, nextAnchor.DisplayName, names[i]]));
        }

        // A drop back into place must not rewrite either setting or evict a saved move.
        var savedResourceValue = _browser.Items[s_resourceKey];
        var savedGroupValue = _browser.Items[s_groupKey];
        Assert.Equal(ResourceOrderMoveResult.Unchanged, await store.MoveResourceAsync(previousAnchor, nextAnchor, after: false, [previousAnchor, nextAnchor]));
        Assert.Equal(ResourceOrderMoveResult.Unchanged, await store.MoveGroupAsync("Tags", nextAnchor.DisplayName, previousAnchor.DisplayName, after: true,
            [previousAnchor.DisplayName, nextAnchor.DisplayName]));
        Assert.Equal(savedResourceValue, _browser.Items[s_resourceKey]);
        Assert.Equal(savedGroupValue, _browser.Items[s_groupKey]);

        var savedResources = (await _browser.Storage.GetAsync<List<RelativeOrderMove>>(s_resourceKey)).Value!;
        var savedGroups = (await _browser.Storage.GetAsync<Dictionary<string, List<RelativeOrderMove>>>(s_groupKey)).Value!;
        Assert.Equal(2, savedGroups.Count);
        Assert.Equal(previousAnchor.PersistentKey, savedResources[^1].PreviousKey);
        Assert.Equal(nextAnchor.PersistentKey, savedResources[^1].NextKey);
        Assert.Equal(names[1..].Select(name => $"{name}_0"), savedResources.Select(move => move.Key));
        Assert.Equal(names[1..], savedGroups.Values.SelectMany(moves => moves).Select(move => move.Key).Order(StringComparer.Ordinal));

        // The real storage implementation encrypts and base64url-encodes these values before returning them over SignalR.
        Assert.InRange(_browser.Items[s_resourceKey].Length, 1, 31 * 1024);
        Assert.InRange(_browser.Items[s_groupKey].Length, 1, 31 * 1024);
    }

    [Fact]
    public async Task GroupOrder_PersistsOnlyMovesAndKeepsGroupingModesIndependent()
    {
        var store = await CreateLoadedStoreAsync();
        Assert.Equal(ResourceOrderMoveResult.Moved, await store.MoveGroupAsync("Tags", "", "Alpha", after: false, ["Alpha", "Beta", ""]));
        Assert.Equal(ResourceOrderMoveResult.Moved, await store.MoveGroupAsync("ResourceType", "Project", "Container", after: false, ["Container", "Project"]));

        var saved = (await _browser.Storage.GetAsync<Dictionary<string, List<RelativeOrderMove>>>(s_groupKey)).Value!;
        Assert.Equal(2, saved.Count);
        Assert.Equal("", Assert.Single(saved["Tags"]).Key);
        Assert.Equal("Project", Assert.Single(saved["ResourceType"]).Key);
        Assert.True((await _browser.Storage.GetUnprotectedAsync<Dictionary<string, List<RelativeOrderMove>>>(s_groupKey)).InvalidValue);
        Assert.Equal([s_groupKey], _browser.Items.Keys);

        var reloaded = await CreateLoadedStoreAsync();
        Assert.Equal(["A new group", "", "Alpha", "Beta"], OrderGroups(reloaded, "Tags", "A new group", "Alpha", "Beta", ""));
        Assert.Equal(["Beta", ""], OrderGroups(reloaded, "Tags", "Beta", ""));
        Assert.Equal(["Project", "Container"], OrderGroups(reloaded, "ResourceType", "Container", "Project"));
        Assert.Equal(["Exited", "Running"], OrderGroups(reloaded, "State", "Exited", "Running"));
    }

    [Fact]
    public async Task GroupOrder_FallsBackToTheNextGroupWhenTheDropTargetIsRemoved()
    {
        var store = await CreateLoadedStoreAsync();
        Assert.Equal(ResourceOrderMoveResult.Moved, await store.MoveGroupAsync("Tags", "", "Api", after: true, ["Api", "Database", "Web", ""]));

        var reloaded = await CreateLoadedStoreAsync();
        Assert.Equal(["Api", "", "Cache", "Database", "Web"], OrderGroups(reloaded, "Tags", "Api", "Cache", "Database", "Web", ""));
        Assert.Equal(["Cache", "", "Database", "Web"], OrderGroups(reloaded, "Tags", "Cache", "Database", "Web", ""));
    }

    [Fact]
    public async Task Order_UnreadableStorageIsNotOverwritten()
    {
        var store = await CreateLoadedStoreAsync();
        var api = CreateResource("api");
        var worker = CreateResource("worker");
        Assert.Equal(ResourceOrderMoveResult.Moved, await store.MoveResourceAsync(worker, api, after: false, [api, worker]));
        var saved = _browser.Items[s_resourceKey];

        _browser.FailReads = true;
        var reloaded = await CreateLoadedStoreAsync();
        var cache = CreateResource("cache");
        Assert.Equal(ResourceOrderMoveResult.NotSaved, await reloaded.MoveResourceAsync(cache, api, after: false, [api, cache, worker]));

        Assert.Equal(["api", "cache", "worker"], GetNames(reloaded, api, cache, worker));
        Assert.Equal(saved, _browser.Items[s_resourceKey]);
    }

    [Fact]
    public async Task Order_UnprotectedMovesAreIgnoredAndReplacedWithoutBackup()
    {
        await _browser.Storage.SetUnprotectedAsync<List<RelativeOrderMove>>(s_resourceKey,
            [new("worker_0", PreviousKey: null, NextKey: "api_0", PreferNext: false)]);
        var api = CreateResource("api");
        var worker = CreateResource("worker");
        var store = await CreateLoadedStoreAsync();
        Assert.Equal(["api", "worker"], GetNames(store, api, worker));

        Assert.Equal(ResourceOrderMoveResult.Moved, await store.MoveResourceAsync(worker, api, after: false, [api, worker]));

        Assert.Equal(["worker", "api"], GetNames(await CreateLoadedStoreAsync(), api, worker));
        Assert.Equal([s_resourceKey], _browser.Items.Keys);
    }

    [Fact]
    public async Task Order_LoadKeepsTheLastMoveWhenReturningAnItemToAnEarlierPosition()
    {
        var first = new RelativeOrderMove("api_0", PreviousKey: "worker_0", NextKey: null, PreferNext: false);
        var second = new RelativeOrderMove("api_0", PreviousKey: null, NextKey: "worker_0", PreferNext: true);
        await _browser.Storage.SetAsync<List<RelativeOrderMove>>(s_resourceKey, [first, second, first]);

        Assert.Equal(["worker", "api"], GetNames(await CreateLoadedStoreAsync(), CreateResource("api"), CreateResource("worker")));
    }

    [Fact]
    public async Task Collapsed_LoadsWithOrderInOneSnapshotAndKeepsExistingValues()
    {
        await _browser.Storage.SetAsync<List<string>>(BrowserStorageKeys.CollapsedResourceNamesKey("ClientApp"), ["api_0"]);
        await _browser.Storage.SetAsync<Dictionary<string, List<string>>>(BrowserStorageKeys.CollapsedResourceGroupsKey("ClientApp"), new() { ["Tags"] = ["web"] });

        var store = await CreateLoadedStoreAsync();

        Assert.Equal(1, _browser.SnapshotReadCount);
        Assert.True(store.IsResourceCollapsed("api_0"));
        Assert.False(store.IsResourceCollapsed("worker_0"));
        Assert.True(store.IsGroupCollapsed("Tags", "WEB"));
        Assert.False(store.IsGroupCollapsed("State", "web"));
    }

    [Fact]
    public async Task Collapsed_ChangesArePublishedSavedAndScopedToTheClientApplication()
    {
        var store = await CreateLoadedStoreAsync();
        var changedCount = 0;
        store.Changed += () => changedCount++;

        Assert.True(await store.SetResourcesCollapsedAsync(["api_0", "worker_0"], collapsed: true));
        Assert.True(await store.SetResourcesCollapsedAsync(["api_0"], collapsed: false));
        Assert.True(await store.ToggleGroupCollapsedAsync("section", "telemetry"));
        Assert.True(await store.ToggleGroupCollapsedAsync("Tags", ""));
        Assert.True(await store.ToggleGroupCollapsedAsync("State", "Running"));
        Assert.True(await store.ToggleGroupCollapsedAsync("State", "Running"));

        Assert.Equal(6, changedCount);
        Assert.Equal(["worker_0"], (await _browser.Storage.GetAsync<List<string>>(BrowserStorageKeys.CollapsedResourceNamesKey("ClientApp"))).Value);
        var storedGroups = (await _browser.Storage.GetAsync<Dictionary<string, List<string>>>(BrowserStorageKeys.CollapsedResourceGroupsKey("ClientApp"))).Value;
        Assert.NotNull(storedGroups);
        Assert.Collection(storedGroups.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            pair =>
            {
                Assert.Equal("Tags", pair.Key);
                Assert.Equal([""], pair.Value);
            },
            pair =>
            {
                Assert.Equal("section", pair.Key);
                Assert.Equal(["telemetry"], pair.Value);
            });

        var reloaded = await CreateLoadedStoreAsync();
        Assert.False(reloaded.IsResourceCollapsed("api_0"));
        Assert.True(reloaded.IsResourceCollapsed("worker_0"));
        Assert.True(reloaded.IsGroupCollapsed("section", "telemetry"));
        Assert.True(reloaded.IsGroupCollapsed("Tags", ""));
        Assert.False(reloaded.IsGroupCollapsed("State", "Running"));

        var otherApplication = await CreateLoadedStoreAsync("OtherApp");
        Assert.False(otherApplication.IsResourceCollapsed("worker_0"));
        Assert.False(otherApplication.IsGroupCollapsed("section", "telemetry"));
    }

    [Fact]
    public async Task Collapsed_ReturnsFalseWhenNotSavedAndKeepsTheCurrentValue()
    {
        var store = await CreateLoadedStoreAsync();
        _browser.FailWrites = true;

        Assert.False(await store.SetResourcesCollapsedAsync(["api_0"], collapsed: true));
        Assert.False(store.IsResourceCollapsed("api_0"));
        Assert.False(await store.ToggleResourceCollapsedAsync("api_0"));
        Assert.False(store.IsResourceCollapsed("api_0"));
        Assert.False(await store.ToggleGroupCollapsedAsync("Tags", "web"));
        Assert.False(store.IsGroupCollapsed("Tags", "web"));
    }

    [Fact]
    public async Task Collapsed_ChangeWhileThePreviousChangeIsSavingIsNotDropped()
    {
        var store = await CreateLoadedStoreAsync();

        await RunWhileFirstWriteIsPendingAsync(
            () => store.SetResourcesCollapsedAsync(["api_0"], collapsed: true),
            () => store.SetResourcesCollapsedAsync(["api_0"], collapsed: false));

        Assert.False(store.IsResourceCollapsed("api_0"));
        Assert.False((await CreateLoadedStoreAsync()).IsResourceCollapsed("api_0"));
    }

    [Fact]
    public async Task Collapsed_ToggleWhileThePreviousToggleIsSavingUndoesIt()
    {
        var store = await CreateLoadedStoreAsync();

        await RunWhileFirstWriteIsPendingAsync(
            () => store.ToggleResourceCollapsedAsync("api_0"),
            () => store.ToggleResourceCollapsedAsync("api_0"));
        await RunWhileFirstWriteIsPendingAsync(
            () => store.ToggleGroupCollapsedAsync("Tags", "web"),
            () => store.ToggleGroupCollapsedAsync("Tags", "web"));

        Assert.False(store.IsResourceCollapsed("api_0"));
        Assert.False(store.IsGroupCollapsed("Tags", "web"));
        var reloaded = await CreateLoadedStoreAsync();
        Assert.False(reloaded.IsResourceCollapsed("api_0"));
        Assert.False(reloaded.IsGroupCollapsed("Tags", "web"));
    }

    // Starts the second change while the first change's write is still pending, then lets the first write finish.
    private async Task RunWhileFirstWriteIsPendingAsync(Func<Task<bool>> first, Func<Task<bool>> second)
    {
        var firstWriteStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeCount = 0;
        _browser.OnAfterWriteAsync = () =>
        {
            if (Interlocked.Increment(ref writeCount) > 1)
            {
                return Task.CompletedTask;
            }

            firstWriteStarted.SetResult();
            return releaseFirstWrite.Task;
        };

        var firstTask = first();
        await firstWriteStarted.Task.DefaultTimeout();
        var secondTask = second();
        releaseFirstWrite.SetResult();
        Assert.True(await firstTask.DefaultTimeout());
        Assert.True(await secondTask.DefaultTimeout());
        _browser.OnAfterWriteAsync = null;
    }

    private static ResourceViewModel CreateResource(string name, string resourceType = "Project", int? order = null, string? parent = null, string? displayName = null)
    {
        var properties = new Dictionary<string, ResourcePropertyViewModel>();
        if (order is { } displayOrder)
        {
            properties.Add(KnownProperties.Resource.DisplayOrder, new(KnownProperties.Resource.DisplayOrder,
                Value.ForString(displayOrder.ToString(CultureInfo.InvariantCulture)), false, null, 0, null, false));
        }
        if (parent is not null)
        {
            properties.Add(KnownProperties.Resource.ParentName, new(KnownProperties.Resource.ParentName,
                Value.ForString(parent), false, null, 0, null, false));
        }

        return ModelTestHelpers.CreateResource(name, displayName: displayName, resourceType: resourceType, properties: properties);
    }

    private static string[] GetNames(ResourceDisplayStore store, params ResourceViewModel[] resources) =>
        store.OrderResources(resources).Select(resource => resource.Name).ToArray();

    private static List<string> OrderGroups(ResourceDisplayStore store, string grouping, params string[] groups) =>
        store.OrderGroups(grouping, groups.ToList(), key => key);

    private async Task<ResourceDisplayStore> CreateLoadedStoreAsync(string clientApplicationName = "ClientApp")
    {
        var store = new ResourceDisplayStore(_browser.Storage, _locks,
            new TestDashboardClient(isEnabled: true, applicationName: clientApplicationName),
            Options.Create(new DashboardOptions { ApplicationName = "TestApp" }), NullLogger<ResourceDisplayStore>.Instance);
        await store.EnsureLoadedAsync().DefaultTimeout();
        return store;
    }
}
