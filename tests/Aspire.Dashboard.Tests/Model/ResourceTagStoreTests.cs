// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Tests.Shared.DashboardModel;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public sealed class ResourceTagStoreTests(ITestOutputHelper testOutputHelper) : IDisposable
{
    private readonly TemporaryWorkspace _workspace = TemporaryWorkspace.Create(testOutputHelper);

    [Fact]
    public void AppHostTagsAreMergedWithoutPersistingDeclaredTags()
    {
        var store = CreateStore();
        store.AddTag("api", "backend");
        store.AddTag("api", "Personal");
        var name = KnownProperties.Resource.TraitPrefix + KnownResourceTraits.DashboardTag;
        var resource = ModelTestHelpers.CreateResource("api", properties: new()
        {
            [name] = new(name, Value.ForList(Value.ForString("Backend"), Value.ForString("API")), false, null, 0, null, false)
        });

        Assert.Equal(["Backend", "API", "Personal"], store.GetTagsByResource([resource])["api"]);
        Assert.Equal(["API", "Backend", "Personal"], store.GetAllTags([resource]));
        Assert.Equal(["backend", "Personal"], store.GetTags("api"));
        Assert.Equal(["backend", "Personal"], store.GetTagsByResource([])["api"]);
    }

    [Fact]
    public void AddTag_AddsTagsInOrderAndRaisesChanged()
    {
        var store = CreateStore();
        var changedCount = 0;
        store.Changed += () => changedCount++;

        Assert.True(store.AddTag("api", "backend"));
        Assert.True(store.AddTag("api", "core"));
        Assert.True(store.AddTag("worker", "backend"));

        Assert.Equal(["backend", "core"], store.GetTags("api"));
        Assert.Equal(["backend"], store.GetTags("worker"));
        Assert.Equal(["backend", "core"], store.GetAllTags());
        Assert.Equal(3, changedCount);
    }

    [Fact]
    public void AddTag_ReusesCasingOfExistingTag()
    {
        var store = CreateStore();

        Assert.True(store.AddTag("api", "Backend"));
        Assert.True(store.AddTag("worker", "  backend "));
        Assert.False(store.AddTag("api", "BACKEND"));

        Assert.Equal(["Backend"], store.GetTags("api"));
        Assert.Equal(["Backend"], store.GetTags("worker"));
        Assert.Equal(["Backend"], store.GetAllTags());
    }

    [Fact]
    public void AddTag_EmptyTagIsIgnored()
    {
        var store = CreateStore();
        var changedCount = 0;
        store.Changed += () => changedCount++;

        Assert.False(store.AddTag("api", "   "));

        Assert.Equal([], store.GetTags("api"));
        Assert.Equal(0, changedCount);
    }

    [Fact]
    public void RemoveTag_RemovesTagIgnoringCase()
    {
        var store = CreateStore();
        store.AddTag("api", "backend");
        store.AddTag("api", "core");

        Assert.True(store.RemoveTag("api", "BACKEND"));
        Assert.False(store.RemoveTag("api", "backend"));
        Assert.False(store.RemoveTag("worker", "core"));

        Assert.Equal(["core"], store.GetTags("api"));
        Assert.Equal(["core"], store.GetAllTags());
    }

    [Fact]
    public void Tags_PersistAcrossStoreInstances()
    {
        var databasePath = Path.Combine(_workspace.Path, DashboardSettingsDatabase.DatabaseFileName);

        var store = new ResourceTagStore(databasePath, NullLogger.Instance);
        store.AddTag("api", "backend");
        store.AddTag("api", "core");
        store.AddTag("worker", "backend");
        store.RemoveTag("api", "core");

        var reloadedStore = new ResourceTagStore(databasePath, NullLogger.Instance);

        var tagsByResource = reloadedStore.GetTagsByResource();
        Assert.Equal(["api", "worker"], tagsByResource.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["backend"], tagsByResource["api"]);
        Assert.Equal(["backend"], tagsByResource["worker"]);
    }

    [Fact]
    public void Tags_WithoutDatabaseAreKeptInMemory()
    {
        var store = new ResourceTagStore(databasePath: null, NullLogger.Instance);

        Assert.True(store.AddTag("api", "backend"));

        Assert.Null(store.DatabasePath);
        Assert.Equal(["backend"], store.GetTags("api"));
    }

    [Fact]
    public void GetDatabasePath_NoPersistenceReturnsNull()
    {
        var options = new DashboardOptions();
        options.Data.PersistenceMode = DashboardPersistenceMode.None;

        Assert.Null(DashboardSettingsDatabase.GetDatabasePath(options));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData("backend", "backend")]
    [InlineData("  my   tag\t ", "my tag")]
    public void NormalizeTag_TrimsAndCollapsesWhitespace(string? tag, string? expected)
    {
        Assert.Equal(expected, ResourceTagStore.NormalizeTag(tag));
    }

    [Fact]
    public void NormalizeTag_TruncatesLongTags()
    {
        var normalized = ResourceTagStore.NormalizeTag(new string('a', ResourceTagStore.MaxTagLength + 10));

        Assert.Equal(new string('a', ResourceTagStore.MaxTagLength), normalized);
    }

    private ResourceTagStore CreateStore() =>
        new(Path.Combine(_workspace.Path, DashboardSettingsDatabase.DatabaseFileName), NullLogger.Instance);

    public void Dispose()
    {
        _workspace.Dispose();
    }
}
