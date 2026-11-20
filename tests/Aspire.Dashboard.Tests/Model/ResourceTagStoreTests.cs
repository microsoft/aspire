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
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public sealed class ResourceTagStoreTests
{
    private static readonly string s_storageKey = BrowserStorageKeys.ResourceTagsKey("TestApp");

    private readonly InMemoryBrowserLocalStorage _browser = new();
    private readonly LocalStorageLocks _locks = new();

    [Fact]
    public void AppHostTagsAreMergedWithoutPersistingDeclaredTags()
    {
        var store = CreateStore();
        store.AddTag("api", "backend");
        store.AddTag("api", "Personal");
        var name = KnownProperties.Resource.TraitPrefix + KnownResourceTraits.Tag;
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

        Assert.True(store.AddTag("api", "backend").Changed);
        Assert.True(store.AddTag("api", "core").Changed);
        Assert.True(store.AddTag("worker", "backend").Changed);

        Assert.Equal(["backend", "core"], store.GetTags("api"));
        Assert.Equal(["backend"], store.GetTags("worker"));
        Assert.Equal(["backend", "core"], store.GetAllTags());
        Assert.Equal(3, changedCount);
    }

    [Fact]
    public void AddTag_ReusesCasingOfExistingTag()
    {
        var store = CreateStore();

        Assert.True(store.AddTag("api", "Backend").Changed);
        Assert.True(store.AddTag("worker", "  backend ").Changed);
        Assert.False(store.AddTag("api", "BACKEND").Changed);

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

        Assert.False(store.AddTag("api", "   ").Changed);

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

        Assert.True((await _browser.Storage.GetUnprotectedAsync<Dictionary<string, List<string>>>(s_storageKey)).InvalidValue);

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
        await reloadedStore.EnsureLoadedAsync().DefaultTimeout();

        Assert.Equal(["backend", "core"], reloadedStore.GetTags("api"));
        Assert.Equal(["backend", "core"], (await CreateLoadedStoreAsync()).GetTags("api"));
    }

    [Fact]
    public async Task Tags_ChangesFromAnotherTabAreKept()
    {
        var tabA = await CreateLoadedStoreAsync();
        var tabB = await CreateLoadedStoreAsync();
        tabA.AddTag("api", "backend");
        tabA.AddTag("cache", "data");
        var tabBChanges = 0;
        tabB.Changed += () => tabBChanges++;

        // Tab B didn't see tab A's tags. Its changes are applied to what tab A saved, including the existing casing.
        tabB.AddTag("worker", "BACKEND");
        tabB.RemoveTag("cache", "data");

        Assert.Equal(["backend"], tabB.GetTags("api"));
        var saved = await CreateLoadedStoreAsync();
        Assert.Equal(["api", "worker"], saved.GetTagsByResource().Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["backend"], saved.GetTags("api"));
        Assert.Equal(["backend"], saved.GetTags("worker"));
        Assert.Equal(3, tabBChanges);
    }

    [Fact]
    public async Task Tags_RejectsMoreThanFiftyAssignmentsEvenAcrossTabs()
    {
        var firstTab = await CreateLoadedStoreAsync();
        var staleTab = await CreateLoadedStoreAsync();
        var names = Enumerable.Range(1, 50).Select(i => FormattableString.Invariant($"resource-{i:D2}")).ToArray();
        foreach (var name in names)
        {
            Assert.True(firstTab.AddTag(name, "backend").Changed);
        }

        Assert.False(firstTab.AddTag(names[0], "extra").Changed);
        Assert.False(firstTab.AddTag("worker", "backend").Changed);
        var staleUpdate = staleTab.AddTag("worker", "backend");
        Assert.True(staleUpdate.Changed);
        Assert.Equal(SettingUpdateResult.LimitReached, await staleUpdate.Completion);
        Assert.Equal([], staleTab.GetTags("worker"));

        var reloaded = await CreateLoadedStoreAsync();
        var saved = reloaded.GetTagsByResource();
        Assert.Equal(names, saved.Keys.Order(StringComparer.Ordinal));
        Assert.All(saved.Values, tags => Assert.Equal(["backend"], tags));

        Assert.True(reloaded.RemoveTag(names[0], "backend"));
        Assert.True(reloaded.AddTag("worker", "backend").Changed);
        Assert.Equal(["backend"], (await CreateLoadedStoreAsync()).GetTags("worker"));
    }

    [Fact]
    public async Task Tags_OverlappingSavesFromTwoTabsKeepBothChanges()
    {
        var tabA = await CreateLoadedStoreAsync();
        var tabB = await CreateLoadedStoreAsync();

        // Tab A's save reads storage, and its response is held, so tab B saves while tab A's save is in progress.
        var releaseTabA = new TaskCompletionSource();
        _browser.OnAfterReadAsync = () => releaseTabA.Task;
        tabA.AddTag("api", "backend");
        _browser.OnAfterReadAsync = null;
        tabB.AddTag("worker", "db");

        // Tab B's save waits for tab A's, then reads what tab A saved instead of the value both tabs started from.
        Assert.False(_browser.Items.ContainsKey(s_storageKey));
        releaseTabA.SetResult();
        await _browser.WaitForProtectedItemAsync<Dictionary<string, List<string>>>(s_storageKey,
            tags => HasTags(tags, "api", "backend") && HasTags(tags, "worker", "db"));
        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => tabB.GetTags("api").SequenceEqual(["backend"]),
            "Tab B should adopt the saved tags.").DefaultTimeout();
    }

    [Fact]
    public async Task Tags_LoadThatReadBeforeASaveDoesNotReplaceIt()
    {
        var store = await CreateLoadedStoreAsync();
        store.AddTag("api", "backend");

        // The load reads storage, but its response is held until a save made after it has completed.
        var releaseLoad = new TaskCompletionSource();
        _browser.OnAfterReadAsync = () => releaseLoad.Task;
        var reloadedStore = CreateStore();
        var load = reloadedStore.EnsureLoadedAsync();
        _browser.OnAfterReadAsync = null;

        reloadedStore.AddTag("worker", "db");
        await _browser.WaitForProtectedItemAsync<Dictionary<string, List<string>>>(s_storageKey,
            tags => HasTags(tags, "api", "backend") && HasTags(tags, "worker", "db"));

        // The load's older value must not replace the saved one in memory.
        releaseLoad.SetResult();
        await load.DefaultTimeout();
        Assert.Equal(["db"], reloadedStore.GetTags("worker"));
        Assert.Equal(["backend"], reloadedStore.GetTags("api"));
    }

    [Fact]
    public async Task Tags_UnreadableStorageIsNotOverwritten()
    {
        var store = await CreateLoadedStoreAsync();
        store.AddTag("api", "backend");
        var saved = _browser.Items[s_storageKey];

        _browser.FailReads = true;
        var reloadedStore = CreateStore();
        await reloadedStore.EnsureLoadedAsync().DefaultTimeout();
        reloadedStore.AddTag("worker", "core");

        // The change is kept in memory, and the tags that couldn't be read aren't replaced.
        Assert.Equal(["core"], reloadedStore.GetTags("worker"));
        Assert.Equal(saved, _browser.Items[s_storageKey]);

        // Reading again can load the stored tags, but it does not save the earlier edit.
        _browser.FailReads = false;
        await reloadedStore.EnsureLoadedAsync().DefaultTimeout();
        Assert.Equal(["backend"], reloadedStore.GetTags("api"));
        Assert.Empty(reloadedStore.GetTags("worker"));
        Assert.Equal(saved, _browser.Items[s_storageKey]);
    }

    [Fact]
    public async Task Tags_AfterFailedSaveOnlyNewEditsAreSaved()
    {
        var store = await CreateLoadedStoreAsync();
        store.AddTag("api", "backend");
        var saved = _browser.Items[s_storageKey];

        _browser.FailReads = true;
        store.AddTag("api", "core");
        Assert.Equal(["backend", "core"], store.GetTags("api"));
        Assert.Equal(saved, _browser.Items[s_storageKey]);

        _browser.FailReads = false;
        store.AddTag("worker", "db");
        var reloaded = await CreateLoadedStoreAsync();
        Assert.Equal(["backend"], reloaded.GetTags("api"));
        Assert.Equal(["db"], reloaded.GetTags("worker"));
    }

    [Fact]
    public async Task Tags_AreScopedToApplicationName()
    {
        var store = await CreateLoadedStoreAsync();
        store.AddTag("api", "backend");

        var otherApp = new ResourceTagStore(_browser.Storage, _locks, Options.Create(new DashboardOptions { ApplicationName = "Other App" }), NullLogger<ResourceTagStore>.Instance);
        await otherApp.EnsureLoadedAsync().DefaultTimeout();

        Assert.Empty(otherApp.GetAllTags());
        Assert.Equal([s_storageKey], _browser.Items.Keys);
    }

    [Theory]
    [InlineData("""{"api":["backend"]}""")]
    [InlineData("""{"api":["backend"]""")]
    public async Task Tags_UnprotectedValueIsIgnoredUntilTheNextChange(string plaintext)
    {
        _browser.Items[s_storageKey] = plaintext;
        var store = await CreateLoadedStoreAsync();
        Assert.Empty(store.GetAllTags());
        Assert.Equal(plaintext, _browser.Items[s_storageKey]);

        store.AddTag("api", "core");

        Assert.Equal(["core"], (await CreateLoadedStoreAsync()).GetTags("api"));
        Assert.Equal([s_storageKey], _browser.Items.Keys);
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

    private static bool HasTags(Dictionary<string, List<string>> tags, string resource, params string[] expected) =>
        tags.TryGetValue(resource, out var saved) && saved.SequenceEqual(expected);

    private ResourceTagStore CreateStore() =>
        new(_browser.Storage, _locks, Options.Create(new DashboardOptions { ApplicationName = "TestApp" }), NullLogger<ResourceTagStore>.Instance);

    private async Task<ResourceTagStore> CreateLoadedStoreAsync()
    {
        var store = CreateStore();
        await store.EnsureLoadedAsync().DefaultTimeout();
        return store;
    }
}
