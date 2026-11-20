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
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public sealed class ResourceOrderStoreTests
{
    private static readonly string s_resourceKey = BrowserStorageKeys.ResourceOrderKey("TestApp");
    private static readonly string s_groupKey = BrowserStorageKeys.GroupOrderKey("TestApp");

    private readonly InMemoryBrowserLocalStorage _browser = new();
    private readonly FakeTimeProvider _timeProvider = new();
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
        Assert.True(store.TryMoveResource(worker, cache, after: false, [api, cache, worker]));

        var saved = Assert.Single((await _browser.Storage.GetAsync<List<RelativeOrderMove>>(s_resourceKey)).Value!);
        Assert.Equal(("worker_0", "api_0", "cache_0"), (saved.Key, saved.PreviousKey, saved.NextKey));
        Assert.True((await _browser.Storage.GetUnprotectedAsync<List<RelativeOrderMove>>(s_resourceKey)).InvalidValue);
        Assert.Equal([s_resourceKey], _browser.Items.Keys);

        var reloaded = await CreateLoadedStoreAsync();
        Assert.Equal(["api", "worker", "auth", "cache"], GetNames(reloaded, api, cache, worker, CreateResource("auth")));
        Assert.Equal(["api", "worker"], GetNames(reloaded, api, worker));
        Assert.Equal(["api", "worker", "cache"], GetNames(reloaded, api, cache, worker));
    }

    [Theory]
    [InlineData(true, true, "api,worker,auth,cache")]
    [InlineData(true, false, "api,worker,auth")]
    [InlineData(false, true, "auth,worker,cache")]
    [InlineData(false, false, "auth,worker")]
    public async Task ResourceOrder_UsesSurvivingNeighbors(bool hasPrevious, bool hasNext, string expected)
    {
        var store = await CreateLoadedStoreAsync();
        var api = CreateResource("api");
        var cache = CreateResource("cache");
        var worker = CreateResource("worker");
        Assert.True(store.TryMoveResource(worker, cache, after: false, [api, cache, worker]));

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
        Assert.True(store.TryMoveResource(worker, api, after: false, [root, other, api, worker]));

        Assert.Equal(["worker", "api", "other", "root"], GetNames(store, root, other, api, worker));
        Assert.Equal(["api", "other", "root", "worker"], GetNames(store, root, other, api, CreateResource("worker", parent: "other")));
        Assert.Equal(["worker", "api", "other", "root"], GetNames(store, root, other, api, worker));
    }

    [Fact]
    public async Task ResourceOrder_PreservesDependentMovesFromDifferentTabs()
    {
        var tabA = await CreateLoadedStoreAsync();
        var tabB = await CreateLoadedStoreAsync();
        var a = CreateResource("a");
        var b = CreateResource("b");
        var c = CreateResource("c");
        var d = CreateResource("d");

        ResourceViewModel[] resources = [a, b, c, d];
        Assert.True(tabA.TryMoveResource(a, c, after: true, resources)); // b, c, a, d
        Assert.True(tabB.TryMoveResource(b, a, after: true, resources)); // c, a, b, d
        Assert.True(tabA.TryMoveResource(a, d, after: true, resources)); // c, b, d, a

        Assert.Equal(["c", "b", "d", "a"], GetNames(await CreateLoadedStoreAsync(), a, b, c, d));
    }

    [Fact]
    public async Task GroupOrder_PersistsOnlyMovesAndKeepsGroupingModesIndependent()
    {
        var store = await CreateLoadedStoreAsync();
        Assert.True(store.TryMoveGroup("Tags", "", "Alpha", after: false, ["Alpha", "Beta", ""]));
        Assert.True(store.TryMoveGroup("ResourceType", "Project", "Container", after: false, ["Container", "Project"]));

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
        Assert.True(store.TryMoveGroup("Tags", "", "Api", after: true, ["Api", "Database", "Web", ""]));

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
        Assert.True(store.TryMoveResource(worker, api, after: false, [api, worker]));
        var saved = _browser.Items[s_resourceKey];

        _browser.FailReads = true;
        var reloaded = await CreateLoadedStoreAsync();
        var cache = CreateResource("cache");
        Assert.True(reloaded.TryMoveResource(cache, api, after: false, [api, cache, worker]));

        Assert.Equal(["cache", "api", "worker"], GetNames(reloaded, api, cache, worker));
        Assert.Equal(saved, _browser.Items[s_resourceKey]);
    }

    [Fact]
    public async Task Order_UnprotectedMovesAreIgnoredAndReplacedWithoutBackup()
    {
        await _browser.Storage.SetUnprotectedAsync<List<RelativeOrderMove>>(s_resourceKey,
            [new(Guid.NewGuid(), "worker_0", PreviousKey: null, NextKey: "api_0")]);
        var api = CreateResource("api");
        var worker = CreateResource("worker");
        var store = await CreateLoadedStoreAsync();
        Assert.Equal(["api", "worker"], GetNames(store, api, worker));

        Assert.True(store.TryMoveResource(worker, api, after: false, [api, worker]));

        Assert.Equal(["worker", "api"], GetNames(await CreateLoadedStoreAsync(), api, worker));
        Assert.Equal([s_resourceKey], _browser.Items.Keys);
    }

    [Fact]
    public async Task Order_DuplicateMoveIdsDoNotReplayTwice()
    {
        var first = new RelativeOrderMove(Guid.NewGuid(), "api_0", PreviousKey: "worker_0", NextKey: null);
        var second = new RelativeOrderMove(Guid.NewGuid(), "api_0", PreviousKey: null, NextKey: "worker_0");
        await _browser.Storage.SetAsync<List<RelativeOrderMove>>(s_resourceKey, [first, second, first]);

        Assert.Equal(["api", "worker"], GetNames(await CreateLoadedStoreAsync(), CreateResource("api"), CreateResource("worker")));
    }

    private static ResourceViewModel CreateResource(string name, string resourceType = "Project", int? order = null, string? parent = null)
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

        return ModelTestHelpers.CreateResource(name, resourceType: resourceType, properties: properties);
    }

    private static string[] GetNames(ResourceOrderStore store, params ResourceViewModel[] resources) =>
        store.OrderResources(resources).Select(resource => resource.Name).ToArray();

    private static List<string> OrderGroups(ResourceOrderStore store, string grouping, params string[] groups) =>
        store.OrderGroups(grouping, groups.ToList(), key => key);

    private async Task<ResourceOrderStore> CreateLoadedStoreAsync()
    {
        var store = new ResourceOrderStore(_browser.Storage, _locks, _timeProvider,
            Options.Create(new DashboardOptions { ApplicationName = "TestApp" }), NullLogger<ResourceOrderStore>.Instance);
        await store.EnsureLoadedAsync().DefaultTimeout();
        return store;
    }
}
