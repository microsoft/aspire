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
    public async Task AppHostTagsAreMergedWithoutPersistingDeclaredTags()
    {
        var store = CreateStore();
        await store.AddTagAsync("api", "backend");
        await store.AddTagAsync("api", "Personal");
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
    public async Task AddTag_AddsTagsInOrderAndRaisesChanged()
    {
        var store = CreateStore();
        var changedCount = 0;
        store.Changed += () => changedCount++;

        Assert.Equal(SettingUpdateResult.Changed, await store.AddTagAsync("api", "backend"));
        Assert.Equal(SettingUpdateResult.Changed, await store.AddTagAsync("api", "core"));
        Assert.Equal(SettingUpdateResult.Changed, await store.AddTagAsync("worker", "backend"));

        Assert.Equal(["backend", "core"], store.GetTags("api"));
        Assert.Equal(["backend"], store.GetTags("worker"));
        Assert.Equal(["backend", "core"], store.GetAllTags());
        Assert.Equal(3, changedCount);
    }

    [Fact]
    public async Task AddTag_ReusesCasingOfExistingTag()
    {
        var store = CreateStore();

        Assert.Equal(SettingUpdateResult.Changed, await store.AddTagAsync("api", "Backend"));
        Assert.Equal(SettingUpdateResult.Changed, await store.AddTagAsync("worker", "  backend "));
        Assert.Equal(SettingUpdateResult.Unchanged, await store.AddTagAsync("api", "BACKEND"));

        Assert.Equal(["Backend"], store.GetTags("api"));
        Assert.Equal(["Backend"], store.GetTags("worker"));
        Assert.Equal(["Backend"], store.GetAllTags());
    }

    [Fact]
    public async Task AddTag_EmptyTagIsIgnored()
    {
        var store = CreateStore();
        var changedCount = 0;
        store.Changed += () => changedCount++;

        Assert.Equal(SettingUpdateResult.Unchanged, await store.AddTagAsync("api", "   "));

        Assert.Equal([], store.GetTags("api"));
        Assert.Equal(0, changedCount);
    }

    [Fact]
    public async Task RemoveTag_RemovesTagIgnoringCase()
    {
        var store = CreateStore();
        await store.AddTagAsync("api", "backend");
        await store.AddTagAsync("api", "core");

        Assert.Equal(SettingUpdateResult.Changed, await store.RemoveTagAsync("api", "BACKEND"));
        Assert.Equal(SettingUpdateResult.Unchanged, await store.RemoveTagAsync("api", "backend"));
        Assert.Equal(SettingUpdateResult.Unchanged, await store.RemoveTagAsync("worker", "core"));

        Assert.Equal(["core"], store.GetTags("api"));
        Assert.Equal(["core"], store.GetAllTags());
    }

    [Fact]
    public async Task Tags_PersistInBrowserLocalStorageAcrossStores()
    {
        var store = await CreateLoadedStoreAsync();
        await store.AddTagAsync("api", "backend");
        await store.AddTagAsync("api", "core");
        await store.AddTagAsync("worker", "backend");
        await store.RemoveTagAsync("api", "core");

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
        await store.AddTagAsync("api", "backend");

        // Adding a tag before the stored tags are read must not replace them.
        var reloadedStore = CreateStore();
        await reloadedStore.AddTagAsync("api", "core");
        await reloadedStore.EnsureLoadedAsync().DefaultTimeout();

        Assert.Equal(["backend", "core"], reloadedStore.GetTags("api"));
        Assert.Equal(["backend", "core"], (await CreateLoadedStoreAsync()).GetTags("api"));
    }

    [Fact]
    public async Task Tags_ChangesFromAnotherTabAreKept()
    {
        var tabA = await CreateLoadedStoreAsync();
        var tabB = await CreateLoadedStoreAsync();
        await tabA.AddTagAsync("api", "backend");
        await tabA.AddTagAsync("cache", "data");
        var tabBChanges = 0;
        tabB.Changed += () => tabBChanges++;

        // Tab B didn't see tab A's tags. Its changes are applied to what tab A saved, including the existing casing.
        await tabB.AddTagAsync("worker", "BACKEND");
        await tabB.RemoveTagAsync("cache", "data");

        Assert.Equal(["backend"], tabB.GetTags("api"));
        var saved = await CreateLoadedStoreAsync();
        Assert.Equal(["api", "worker"], saved.GetTagsByResource().Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["backend"], saved.GetTags("api"));
        Assert.Equal(["backend"], saved.GetTags("worker"));
        Assert.Equal(2, tabBChanges);
    }

    [Fact]
    public async Task RemoveTag_AlreadyRemovedInAnotherTabRefreshesWithoutWriting()
    {
        var tabA = await CreateLoadedStoreAsync();
        await tabA.AddTagAsync("api", "backend");
        var tabB = await CreateLoadedStoreAsync();
        Assert.Equal(["backend"], tabB.GetTags("api"));
        Assert.Equal(SettingUpdateResult.Changed, await tabA.RemoveTagAsync("api", "backend"));
        var saved = _browser.Items[s_storageKey];

        _browser.FailWrites = true;
        Assert.Equal(SettingUpdateResult.Unchanged, await tabB.RemoveTagAsync("api", "backend"));
        Assert.Empty(tabB.GetTags("api"));
        Assert.Equal(saved, _browser.Items[s_storageKey]);
    }

    [Fact]
    public async Task Tags_RejectsMoreThanFiftyAssignmentsEvenAcrossTabs()
    {
        var firstTab = await CreateLoadedStoreAsync();
        var staleTab = await CreateLoadedStoreAsync();
        var names = Enumerable.Range(1, 50).Select(i => FormattableString.Invariant($"resource-{i:D2}")).ToArray();
        foreach (var name in names)
        {
            Assert.Equal(SettingUpdateResult.Changed, await firstTab.AddTagAsync(name, "backend"));
        }

        Assert.Equal(SettingUpdateResult.LimitReached, await firstTab.AddTagAsync(names[0], "extra"));
        Assert.Equal(SettingUpdateResult.LimitReached, await firstTab.AddTagAsync("worker", "backend"));
        Assert.Equal(SettingUpdateResult.LimitReached, await staleTab.AddTagAsync("worker", "backend"));
        Assert.Equal(["backend"], staleTab.GetTags(names[0]));
        Assert.Equal([], staleTab.GetTags("worker"));

        var reloaded = await CreateLoadedStoreAsync();
        var saved = reloaded.GetTagsByResource();
        Assert.Equal(names, saved.Keys.Order(StringComparer.Ordinal));
        Assert.All(saved.Values, tags => Assert.Equal(["backend"], tags));

        Assert.Equal(SettingUpdateResult.Changed, await reloaded.RemoveTagAsync(names[0], "backend"));
        Assert.Equal(SettingUpdateResult.Changed, await reloaded.AddTagAsync("worker", "backend"));
        Assert.Equal(["backend"], (await CreateLoadedStoreAsync()).GetTags("worker"));
    }

    [Fact]
    public async Task Tags_OverlappingSavesFromTwoTabsKeepBothChanges()
    {
        var tabA = await CreateLoadedStoreAsync();
        var tabB = await CreateLoadedStoreAsync();

        // Tab A's save reads storage, and its response is held, so tab B saves while tab A's save is in progress.
        var releaseTabA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _browser.OnAfterReadAsync = () => releaseTabA.Task;
        var saveA = tabA.AddTagAsync("api", "backend");
        _browser.OnAfterReadAsync = null;
        var saveB = tabB.AddTagAsync("worker", "db");

        // Tab B's save waits for tab A's, then reads what tab A saved instead of the value both tabs started from.
        try
        {
            Assert.False(saveB.IsCompleted);
        }
        finally
        {
            releaseTabA.SetResult();
        }

        Assert.Equal([SettingUpdateResult.Changed, SettingUpdateResult.Changed], await Task.WhenAll(saveA, saveB).DefaultTimeout());
        var reloaded = await CreateLoadedStoreAsync();
        Assert.Equal(["backend"], reloaded.GetTags("api"));
        Assert.Equal(["db"], reloaded.GetTags("worker"));
        Assert.Equal(["backend"], tabB.GetTags("api"));
    }

    [Fact]
    public async Task Tags_LoadThatReadBeforeASaveDoesNotReplaceIt()
    {
        var store = await CreateLoadedStoreAsync();
        await store.AddTagAsync("api", "backend");

        // The load reads storage, but its response is held until a save made after it has completed.
        var releaseLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _browser.OnAfterReadAsync = () => releaseLoad.Task;
        var reloadedStore = CreateStore();
        var load = reloadedStore.EnsureLoadedAsync();
        _browser.OnAfterReadAsync = null;

        Assert.Equal(SettingUpdateResult.Changed, await reloadedStore.AddTagAsync("worker", "db"));

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
        await store.AddTagAsync("api", "backend");
        var saved = _browser.Items[s_storageKey];

        _browser.FailReads = true;
        var reloadedStore = CreateStore();
        await reloadedStore.EnsureLoadedAsync().DefaultTimeout();
        Assert.Equal(SettingUpdateResult.NotSaved, await reloadedStore.AddTagAsync("worker", "core"));
        Assert.Empty(reloadedStore.GetTags("worker"));
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
        await store.AddTagAsync("api", "backend");
        var saved = _browser.Items[s_storageKey];

        _browser.FailReads = true;
        Assert.Equal(SettingUpdateResult.NotSaved, await store.AddTagAsync("api", "core"));
        Assert.Equal(["backend"], store.GetTags("api"));
        Assert.Equal(saved, _browser.Items[s_storageKey]);

        _browser.FailReads = false;
        await store.AddTagAsync("worker", "db");
        var reloaded = await CreateLoadedStoreAsync();
        Assert.Equal(["backend"], reloaded.GetTags("api"));
        Assert.Equal(["db"], reloaded.GetTags("worker"));
    }

    [Fact]
    public async Task Tags_AreScopedToApplicationName()
    {
        var store = await CreateLoadedStoreAsync();
        await store.AddTagAsync("api", "backend");

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

        await store.AddTagAsync("api", "core");

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

    private ResourceTagStore CreateStore() =>
        new(_browser.Storage, _locks, Options.Create(new DashboardOptions { ApplicationName = "TestApp" }), NullLogger<ResourceTagStore>.Instance);

    private async Task<ResourceTagStore> CreateLoadedStoreAsync()
    {
        var store = CreateStore();
        await store.EnsureLoadedAsync().DefaultTimeout();
        return store;
    }
}
