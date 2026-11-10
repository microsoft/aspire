// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Model;
using Aspire.Tests.Shared.DashboardModel;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public sealed class ResourceOrderStoreTests(ITestOutputHelper testOutputHelper) : IDisposable
{
    [Fact]
    public void AppHostDisplayOrdersSeedOrderingAndSavedMovesTakePrecedence()
    {
        var databasePath = Path.Combine(_workspace.Path, DashboardSettingsDatabase.DatabaseFileName);
        var store = new ResourceOrderStore(databasePath, NullLogger.Instance);
        var api = CreateResourceWithDisplayOrder("api", 10);
        var cache = CreateResourceWithDisplayOrder("cache", 20);
        var worker = ModelTestHelpers.CreateResource("worker");
        ResourceViewModel[] resources = [api, cache, worker];
        store.EnsureResources(resources.Select(r => r.PersistentKey));

        Assert.Equal([cache.PersistentKey, api.PersistentKey, worker.PersistentKey], GetOrderedKeys(store, resources));

        // A restart must not turn inferred order into a user customization. Higher orders come first and resources
        // without one count as 0, so a negative order moves cache after worker.
        store = new ResourceOrderStore(databasePath, NullLogger.Instance);
        resources = [api, CreateResourceWithDisplayOrder("cache", -10), worker];
        store.EnsureResources(resources.Select(r => r.PersistentKey));
        Assert.Equal([api.PersistentKey, worker.PersistentKey, cache.PersistentKey], GetOrderedKeys(store, resources));
        Assert.True(store.TryMove(cache.PersistentKey, api.PersistentKey, false, resources));

        store = new ResourceOrderStore(databasePath, NullLogger.Instance);
        store.EnsureResources(resources.Select(r => r.PersistentKey));
        Assert.Equal([cache.PersistentKey, api.PersistentKey, worker.PersistentKey], GetOrderedKeys(store, resources));
    }

    [Fact]
    public void DisplayOrdersAreScopedToTheRequestingRun()
    {
        var store = new ResourceOrderStore(databasePath: null, NullLogger.Instance);
        ResourceViewModel[] liveRun = [CreateResourceWithDisplayOrder("api", 0), CreateResourceWithDisplayOrder("cache", 10)];
        ResourceViewModel[] recordedRun = [CreateResourceWithDisplayOrder("api", 10), CreateResourceWithDisplayOrder("cache", 0)];
        store.EnsureResources(liveRun.Select(r => r.PersistentKey));

        Assert.Equal(["api_0", "cache_0"], GetOrderedKeys(store, recordedRun));
        Assert.Equal(["cache_0", "api_0"], GetOrderedKeys(store, liveRun));
    }

    [Fact]
    public void ExplicitZeroDisplayOrder_KeepsStoredOrderLikeUnsetOrder()
    {
        var store = new ResourceOrderStore(databasePath: null, NullLogger.Instance);
        // worker is stored after api. Treating its explicit 0 differently from api's unset order would move it first.
        ResourceViewModel[] resources =
        [
            ModelTestHelpers.CreateResource("api"),
            CreateResourceWithDisplayOrder("worker", 0),
            CreateResourceWithDisplayOrder("cache", 10),
            CreateResourceWithDisplayOrder("db", -10)
        ];
        store.EnsureResources(resources.Select(r => r.PersistentKey));

        Assert.Equal(["cache_0", "api_0", "worker_0", "db_0"], GetOrderedKeys(store, resources));
    }

    [Fact]
    public void TryMove_EqualDisplayOrders_UsesDisplayedOrder()
    {
        var store = new ResourceOrderStore(databasePath: null, NullLogger.Instance);
        // Inserted out of name order, with b and a sharing a position. The pane displays a, b, c.
        ResourceViewModel[] resources =
        [
            CreateResourceWithDisplayOrder("b", 5, "Container"),
            CreateResourceWithDisplayOrder("a", 5, "Project"),
            CreateResourceWithDisplayOrder("c", -10, "Project")
        ];
        store.EnsureResources(resources.Select(r => r.PersistentKey));

        Assert.True(store.TryMove("c_0", "a_0", after: true, resources));

        Assert.Equal(["a_0", "c_0", "b_0"], GetOrderedKeys(store));
    }

    private readonly TemporaryWorkspace _workspace = TemporaryWorkspace.Create(testOutputHelper);

    [Fact]
    public void GroupOrder_UpgradesExistingSettingsAndPersistsIndependentScopes()
    {
        var path = Path.Combine(_workspace.Path, DashboardSettingsDatabase.DatabaseFileName);
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE resource_order (resource_key TEXT NOT NULL COLLATE NOCASE PRIMARY KEY, position INTEGER NOT NULL);
                INSERT INTO resource_order VALUES ('cache_0', 0), ('api_0', 1);
                """;
            command.ExecuteNonQuery();
        }
        var store = new ResourceOrderStore(path, NullLogger.Instance);
        store.EnsureGroups("Tags", ["Container", "Untagged", ""]);
        store.EnsureGroups("ResourceType", ["Container", "Project"]);
        store.EnsureGroups("State", ["Running", "Waiting"]);
        Assert.True(store.TryMoveGroup("Tags", "", "Container", after: false));
        Assert.True(store.TryMoveGroup("ResourceType", "Project", "Container", after: false));
        Assert.True(store.TryMoveGroup("State", "Waiting", "Running", after: false));

        var reloaded = new ResourceOrderStore(path, NullLogger.Instance);
        reloaded.EnsureGroups("Tags", ["Container", "A new group"]);
        Assert.Equal(["", "Container", "Untagged", "A new group"], GetOrderedGroupKeys(reloaded, "Tags"));
        Assert.Equal(["Project", "Container"], GetOrderedGroupKeys(reloaded, "ResourceType"));
        Assert.Equal(["Waiting", "Running"], GetOrderedGroupKeys(reloaded, "State"));
        Assert.Equal(["cache_0", "api_0"], GetOrderedKeys(reloaded));
        var tags = new ResourceTagStore(path, NullLogger.Instance);
        Assert.True(tags.AddTag("api", "Container"));
        Assert.Equal(["Container"], new ResourceTagStore(path, NullLogger.Instance).GetTags("api"));
        Assert.Equal(["", "Container", "Untagged", "A new group"],
            GetOrderedGroupKeys(new ResourceOrderStore(path, NullLogger.Instance), "Tags"));
    }

    [Fact]
    public void GroupOrder_ConcurrentUpdatesKeepUniquePositionsInEachScope()
    {
        var store = new ResourceOrderStore(databasePath: null, NullLogger.Instance);
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
    public void GroupOrder_SaveFailurePreservesGroupAndResourcePositions()
    {
        var path = Path.Combine(_workspace.Path, DashboardSettingsDatabase.DatabaseFileName);
        var store = new ResourceOrderStore(path, NullLogger.Instance);
        store.EnsureGroups("Tags", ["backend", "frontend"]);
        store.EnsureResources(["api_0", "cache_0"]);
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE resource_group_order";
            command.ExecuteNonQuery();
        }

        Assert.False(store.TryMoveGroup("Tags", "frontend", "backend", after: false));
        Assert.Equal(["backend", "frontend"], GetOrderedGroupKeys(store, "Tags"));
        Assert.Equal(["api_0", "cache_0"], GetOrderedKeys(store));
    }

    [Fact]
    public void Order_PersistsAlongsideTagsAndAppendsNewResources()
    {
        var path = Path.Combine(_workspace.Path, DashboardSettingsDatabase.DatabaseFileName);
        var tags = new ResourceTagStore(path, NullLogger.Instance);
        tags.AddTag("api", "backend");
        var store = new ResourceOrderStore(path, NullLogger.Instance);
        store.EnsureResources(["api_0", "worker_0", "api_1"]);
        Assert.True(store.TryMove("api_1", "worker_0", after: false));

        var reloaded = new ResourceOrderStore(path, NullLogger.Instance);
        reloaded.EnsureResources(["worker_0", "new_0", "api_0"]);
        Assert.Equal(["api_0", "api_1", "worker_0", "new_0"], GetOrderedKeys(reloaded));
        Assert.Equal(["backend"], new ResourceTagStore(path, NullLogger.Instance).GetTags("api"));
        Assert.Equal(["api_0", "api_1", "worker_0", "new_0"], GetOrderedKeys(new ResourceOrderStore(path, NullLogger.Instance)));
    }

    [Fact]
    public void Order_WithoutDatabaseSupportsMovingBeforeAndAfter()
    {
        var store = new ResourceOrderStore(databasePath: null, NullLogger.Instance);
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
        var store = new ResourceOrderStore(databasePath: null, NullLogger.Instance);
        store.EnsureResources(["API_0", "worker_0", "worker_1"]);
        store.EnsureResources(["WORKER_0", "new_0", "NEW_0"]);
        Assert.Equal(["API_0", "worker_0", "worker_1", "new_0"], GetOrderedKeys(store));
    }

    [Fact]
    public void EnsureResources_ConcurrentUpdatesHaveUniquePositions()
    {
        var store = new ResourceOrderStore(databasePath: null, NullLogger.Instance);
        Parallel.For(0, 100, i => store.EnsureResources([$"resource_{i}", "shared_0"]));

        var positions = store.GetPositions();
        Assert.Equal(101, positions.Count);
        Assert.Equal(Enumerable.Range(0, 101).Select(i => (long)i), positions.Values.Order());
        Assert.All(Enumerable.Range(0, 100), i => Assert.True(positions.ContainsKey($"resource_{i}")));
    }

    [Fact]
    public void TryMove_SaveFailureKeepsOriginalOrder()
    {
        var path = Path.Combine(_workspace.Path, DashboardSettingsDatabase.DatabaseFileName);
        var store = new ResourceOrderStore(path, NullLogger.Instance);
        store.EnsureResources(["api_0", "cache_0"]);
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE resource_order";
            command.ExecuteNonQuery();
        }

        Assert.False(store.TryMove("cache_0", "api_0", after: false));
        Assert.Equal(["api_0", "cache_0"], GetOrderedKeys(store));
    }

    [Fact]
    public void TryMove_UnknownKeyIsRejected()
    {
        var store = new ResourceOrderStore(databasePath: null, NullLogger.Instance);
        store.EnsureResources(["api_0"]);

        Assert.Throws<ArgumentException>(() => store.TryMove("missing_0", "api_0", after: false));
        Assert.Equal(["api_0"], GetOrderedKeys(store));
    }

    private static ResourceViewModel CreateResourceWithDisplayOrder(string name, int order, string resourceType = "Container") =>
        ModelTestHelpers.CreateResource(name, resourceType: resourceType, properties: new()
        {
            [KnownProperties.Resource.DisplayOrder] = new(KnownProperties.Resource.DisplayOrder, Value.ForString(order.ToString(CultureInfo.InvariantCulture)), false, null, 0, null, false)
        });

    private static string[] GetOrderedKeys(ResourceOrderStore store, IEnumerable<ResourceViewModel> resources) =>
        store.GetPositions(resources).OrderBy(pair => pair.Value).Select(pair => pair.Key).ToArray();

    private static string[] GetOrderedKeys(ResourceOrderStore store) =>
        store.GetPositions().OrderBy(pair => pair.Value).Select(pair => pair.Key).ToArray();

    private static string[] GetOrderedGroupKeys(ResourceOrderStore store, string grouping) =>
        store.GetGroupPositions(grouping).OrderBy(pair => pair.Value).Select(pair => pair.Key).ToArray();

    public void Dispose() => _workspace.Dispose();
}
