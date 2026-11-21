// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.BrowserStorage;
using Aspire.Dashboard.Utils;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Aspire.Dashboard.Components.Tests.Controls;

public class ResourceTagEditorTests : DashboardTestContext
{
    [Fact]
    public async Task AppHostTagsAreLockedAndOnlyUserTagsCanBeRemoved()
    {
        var store = Services.GetRequiredService<ResourceTagStore>();
        await store.AddTagAsync("api", "Personal");
        await store.AddTagAsync("api", "backend");
        var cut = Render<ResourceTagEditor>(p => p.Add(c => c.ResourceName, "api")
            .Add(c => c.AppHostTags, new[] { "Backend", "API" }));

        Assert.Equal(["Backend", "API", "Personal"], cut.FindAll(".tag-chip-name").Select(c => c.TextContent));
        Assert.Single(cut.FindAll(".tag-chip-remove"));
        Assert.Equal(["Defined by the AppHost. This tag cannot be removed.", "Defined by the AppHost. This tag cannot be removed."],
            cut.FindAll(".tag-chip-lock[role=img]").Select(e => e.GetAttribute("aria-label")));
        cut.Find(".tag-add").Click();
        cut.Find("[role=combobox]").KeyDown("Backspace");
        cut.Find("[role=combobox]").KeyDown("Backspace");

        Assert.Equal(["Backend", "API"], cut.FindAll(".tag-chip-name").Select(c => c.TextContent));
        Assert.Empty(cut.FindAll(".tag-chip-remove"));
        Assert.Equal(["backend"], store.GetTags("api"));
    }

    private readonly TestLocalStorage _localStorage = new();
    private readonly ResourceTagStore _tagStore;

    [Fact]
    public void AppHostTagsFromOtherResourcesCanBeAddedAsEditableUserTags()
    {
        var cut = Render<ResourceTagEditor>(p => p.Add(c => c.ResourceName, "api")
            .Add(c => c.AvailableTags, new[] { "Backend" }));
        cut.Find(".tag-add").Click();
        cut.Find(".tag-input").Input("back");

        Assert.Equal(["Backend", "Create tag \"back\""], cut.FindAll("[role=option]").Select(o => o.TextContent.Trim()));
        cut.Find(".tag-input").KeyDown("Enter");
        Assert.Equal(["Backend"], _tagStore.GetTags("api"));
        Assert.Single(cut.FindAll(".tag-chip-remove"));
    }

    public ResourceTagEditorTests()
    {
        _tagStore = new(_localStorage, new LocalStorageLocks(), Options.Create(new DashboardOptions()), NullLogger<ResourceTagStore>.Instance);
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        Services.AddLocalization();
        Services.AddSingleton(_tagStore);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddTag_SavesToProtectedStorageOnlyWhenReadable(bool readFailed)
    {
        // As while the circuit is disconnected: saving would replace stored tags that couldn't be read.
        _localStorage.IsReadFailed = _ => readFailed;
        var writes = new ConcurrentQueue<(string Key, bool Protected)>();
        _localStorage.OnSetAsync = (key, _) => writes.Enqueue((key, true));
        _localStorage.OnSetUnprotectedAsync = (key, _) => writes.Enqueue((key, false));
        var toasts = Render<FluentToastProvider>();
        var cut = RenderEditor("api");

        cut.Find(".tag-add").Click();
        cut.Find(".tag-input").Input("backend");
        await cut.Find(".tag-input").KeyDownAsync(new KeyboardEventArgs { Key = "Enter" });

        Assert.Equal(readFailed ? [] : ["backend"], cut.FindAll(".tag-chip-name").Select(c => c.TextContent));
        Assert.Equal(readFailed ? [] : ["backend"], _tagStore.GetTags("api"));
        Assert.Equal(readFailed ? "backend" : string.Empty, cut.Find(".tag-input").GetAttribute("value"));
        if (readFailed)
        {
            var toast = Assert.Single(toasts.FindComponents<FluentToast>()).Instance;
            Assert.Equal(ToastIntent.Warning, toast.Intent);
            Assert.Equal("Couldn't save the change. Check that browser storage is available and try again.", toast.Title);
        }
        else
        {
            Assert.Empty(toasts.FindComponents<FluentToast>());
        }
        var storageKey = BrowserStorageKeys.ResourceTagsKey(new DashboardOptions().GetApplicationNameOrDefault());
        Assert.Equal(readFailed ? [] : [(storageKey, true)], writes);
    }

    [Fact]
    public async Task Render_ShowsResourceTags()
    {
        await _tagStore.AddTagAsync("api", "backend");
        await _tagStore.AddTagAsync("api", "core");
        await _tagStore.AddTagAsync("worker", "jobs");

        var cut = RenderEditor("api");

        Assert.Equal(["backend", "core"], cut.FindAll(".tag-chip-name").Select(e => e.TextContent));
        Assert.Equal(new[] { "backend", "core" }.Select(ColorGenerator.TagInstance.GetColorVariableByKey),
            cut.FindComponents<FluentIcon<Icons.Filled.Size16.Tag>>()
                .Select(icon => icon.Instance.CustomColor));
    }

    [Fact]
    public async Task Input_SuggestsExistingTagsAndCreateOption()
    {
        await _tagStore.AddTagAsync("api", "backend");
        await _tagStore.AddTagAsync("worker", "background");
        await _tagStore.AddTagAsync("worker", "jobs");

        var cut = RenderEditor("api");
        cut.Find(".tag-add").Click();
        cut.Find(".tag-input").Input("ba");

        var options = cut.FindAll("[role='option']");
        Assert.Equal(["background", "Create tag \"ba\""], options.Select(o => o.TextContent.Trim()));
        Assert.Equal("true", options[0].GetAttribute("aria-selected"));
        Assert.Equal(options[0].Id, cut.Find(".tag-input").GetAttribute("aria-activedescendant"));
    }

    [Fact]
    public async Task Enter_AddsActiveSuggestionAndKeepsEditorOpen()
    {
        await _tagStore.AddTagAsync("worker", "Background");

        var cut = RenderEditor("api");
        cut.Find(".tag-add").Click();
        cut.Find(".tag-input").Input("back");
        cut.Find(".tag-input").KeyDown("Enter");

        Assert.Equal(["Background"], _tagStore.GetTags("api"));
        Assert.Equal(["Background"], cut.FindAll(".tag-chip-name").Select(e => e.TextContent));
        Assert.Equal(string.Empty, cut.Find(".tag-input").GetAttribute("value"));
    }

    [Fact]
    public void Enter_CreatesNewTag()
    {
        var cut = RenderEditor("api");
        cut.Find(".tag-add").Click();
        cut.Find(".tag-input").Input("  new   tag ");
        cut.Find(".tag-input").KeyDown("Enter");

        Assert.Equal(["new tag"], _tagStore.GetTags("api"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Enter_AtTagLimitShowsWarningAndKeepsInput(bool filledByAnotherTab)
    {
        var storageKey = BrowserStorageKeys.ResourceTagsKey(new DashboardOptions().GetApplicationNameOrDefault());
        var savedTags = Enumerable.Range(1, 50).ToDictionary(i => $"resource-{i}", _ => new List<string> { "backend" });
        if (!filledByAnotherTab)
        {
            await _localStorage.SetAsync(storageKey, savedTags);
        }

        var toasts = Render<FluentToastProvider>();
        var cut = RenderEditor("api");
        if (filledByAnotherTab)
        {
            await _localStorage.SetAsync(storageKey, savedTags);
        }

        cut.Find(".tag-add").Click();
        cut.Find(".tag-input").Input("new tag");
        await cut.Find(".tag-input").KeyDownAsync(new KeyboardEventArgs { Key = "Enter" });

        var toast = Assert.Single(toasts.FindComponents<FluentToast>()).Instance;
        Assert.Equal(ToastIntent.Warning, toast.Intent);
        Assert.Equal("Can't add this tag. The limit is 50 tag assignments per application. Remove an existing tag assignment first.", toast.Title);
        Assert.Equal("new tag", cut.Find(".tag-input").GetAttribute("value"));
        Assert.Empty(cut.FindAll(".tag-chip-name"));
        Assert.Empty(_tagStore.GetTags("api"));
    }

    [Fact]
    public async Task RemoveButton_RemovesTag()
    {
        await _tagStore.AddTagAsync("api", "backend");
        await _tagStore.AddTagAsync("api", "core");

        var cut = RenderEditor("api");
        cut.Find("button[aria-label='Remove tag backend']").Click();

        Assert.Equal(["core"], _tagStore.GetTags("api"));
        Assert.Equal(["core"], cut.FindAll(".tag-chip-name").Select(e => e.TextContent));
    }

    [Fact]
    public async Task Backspace_InEmptyInputRemovesLastTag()
    {
        await _tagStore.AddTagAsync("api", "backend");
        await _tagStore.AddTagAsync("api", "core");

        var cut = RenderEditor("api");
        cut.Find(".tag-add").Click();
        cut.Find(".tag-input").KeyDown("Backspace");

        Assert.Equal(["backend"], _tagStore.GetTags("api"));
    }

    [Fact]
    public async Task TagStoreChange_UpdatesChips()
    {
        var cut = RenderEditor("api");

        await _tagStore.AddTagAsync("api", "backend");

        cut.WaitForAssertion(() => Assert.Equal(["backend"], cut.FindAll(".tag-chip-name").Select(e => e.TextContent)));
    }

    private IRenderedComponent<ResourceTagEditor> RenderEditor(string resourceName) =>
        Render<ResourceTagEditor>(builder => builder.Add(p => p.ResourceName, resourceName));
}
