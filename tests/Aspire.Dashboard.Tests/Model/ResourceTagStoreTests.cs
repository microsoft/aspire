// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Dashboard.Utils;
using Aspire.Tests.Shared.DashboardModel;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public sealed class ResourceTagStoreTests
{
    private static readonly string s_storageKey = BrowserStorageKeys.ResourceTagsKey("TestApp");

    private readonly InMemoryBrowserLocalStorage _browser = new();

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
    public async Task Tags_PersistInBrowserLocalStorageAcrossStores()
    {
        var store = await CreateLoadedStoreAsync();
        store.AddTag("api", "backend");
        store.AddTag("api", "core");
        store.AddTag("worker", "backend");
        store.RemoveTag("api", "core");

        Assert.Equal("""{"api":["backend"],"worker":["backend"]}""", _browser.Items[s_storageKey]);

        // A new store reads the saved tags, as a new circuit does after a refresh or a restart.
        var reloadedStore = CreateStore();
        var changedCount = 0;
        reloadedStore.Changed += () => changedCount++;
        Assert.Empty(reloadedStore.GetTagsByResource());

        await reloadedStore.EnsureLoadedAsync().DefaultTimeout();

        var tagsByResource = reloadedStore.GetTagsByResource();
        Assert.Equal(["api", "worker"], tagsByResource.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["backend"], tagsByResource["api"]);
        Assert.Equal(["backend"], tagsByResource["worker"]);
        Assert.Equal(1, changedCount);
    }

    [Fact]
    public async Task Tags_AddedBeforeLoadAreMergedWithStoredTags()
    {
        var store = await CreateLoadedStoreAsync();
        store.AddTag("api", "backend");

        // Adding a tag before the stored tags are read must not replace them.
        var reloadedStore = CreateStore();
        reloadedStore.AddTag("api", "core");
        reloadedStore.AddTag("worker", "BACKEND");
        await reloadedStore.EnsureLoadedAsync().DefaultTimeout();

        Assert.Equal(["backend", "core"], reloadedStore.GetTags("api"));
        Assert.Equal(["BACKEND"], reloadedStore.GetTags("worker"));

        var thirdStore = CreateStore();
        await thirdStore.EnsureLoadedAsync().DefaultTimeout();
        Assert.Equal(["backend", "core"], thirdStore.GetTags("api"));
        Assert.Equal(["BACKEND"], thirdStore.GetTags("worker"));
    }

    [Fact]
    public async Task Tags_AreNotSavedUntilStoredTagsAreRead()
    {
        var store = CreateStore();
        store.AddTag("api", "backend");

        Assert.Empty(_browser.Items);

        await store.EnsureLoadedAsync().DefaultTimeout();
        Assert.Equal("""{"api":["backend"]}""", _browser.Items[s_storageKey]);
    }

    [Fact]
    public async Task Tags_AreScopedToApplicationName()
    {
        var store = await CreateLoadedStoreAsync();
        store.AddTag("api", "backend");

        var otherApp = new ResourceTagStore(_browser.Storage, Options.Create(new DashboardOptions { ApplicationName = "Other App" }), NullLogger<ResourceTagStore>.Instance);
        await otherApp.EnsureLoadedAsync().DefaultTimeout();

        Assert.Empty(otherApp.GetAllTags());
        Assert.Equal([s_storageKey], _browser.Items.Keys);
    }

    [Fact]
    public async Task Tags_InvalidStoredValueIsIgnored()
    {
        _browser.Items[s_storageKey] = """{"api":"backend"}""";
        var store = CreateStore();

        await store.EnsureLoadedAsync().DefaultTimeout();
        Assert.Empty(store.GetAllTags());

        store.AddTag("api", "core");
        Assert.Equal("""{"api":["core"]}""", _browser.Items[s_storageKey]);
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
        new(_browser.Storage, Options.Create(new DashboardOptions { ApplicationName = "TestApp" }), NullLogger<ResourceTagStore>.Instance);

    private async Task<ResourceTagStore> CreateLoadedStoreAsync()
    {
        var store = CreateStore();
        await store.EnsureLoadedAsync().DefaultTimeout();
        return store;
    }
}
