// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

public class ResourceTagEditorTests : DashboardTestContext
{
    [Fact]
    public void AppHostTagsAreLockedAndOnlyUserTagsCanBeRemoved()
    {
        var store = Services.GetRequiredService<ResourceTagStore>();
        store.AddTag("api", "Personal");
        store.AddTag("api", "backend");
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

    private readonly ResourceTagStore _tagStore = new(databasePath: null, NullLogger.Instance);

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
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        Services.AddLocalization();
        Services.AddSingleton(_tagStore);
    }

    [Fact]
    public void Render_ShowsResourceTags()
    {
        _tagStore.AddTag("api", "backend");
        _tagStore.AddTag("api", "core");
        _tagStore.AddTag("worker", "jobs");

        var cut = RenderEditor("api");

        Assert.Equal(["backend", "core"], cut.FindAll(".tag-chip-name").Select(e => e.TextContent));
    }

    [Fact]
    public void Input_SuggestsExistingTagsAndCreateOption()
    {
        _tagStore.AddTag("api", "backend");
        _tagStore.AddTag("worker", "background");
        _tagStore.AddTag("worker", "jobs");

        var cut = RenderEditor("api");
        cut.Find(".tag-add").Click();
        cut.Find(".tag-input").Input("ba");

        var options = cut.FindAll("[role='option']");
        Assert.Equal(["background", "Create tag \"ba\""], options.Select(o => o.TextContent.Trim()));
        Assert.Equal("true", options[0].GetAttribute("aria-selected"));
        Assert.Equal(options[0].Id, cut.Find(".tag-input").GetAttribute("aria-activedescendant"));
    }

    [Fact]
    public void Enter_AddsActiveSuggestionAndKeepsEditorOpen()
    {
        _tagStore.AddTag("worker", "Background");

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

    [Fact]
    public void RemoveButton_RemovesTag()
    {
        _tagStore.AddTag("api", "backend");
        _tagStore.AddTag("api", "core");

        var cut = RenderEditor("api");
        cut.Find("button[aria-label='Remove tag backend']").Click();

        Assert.Equal(["core"], _tagStore.GetTags("api"));
        Assert.Equal(["core"], cut.FindAll(".tag-chip-name").Select(e => e.TextContent));
    }

    [Fact]
    public void Backspace_InEmptyInputRemovesLastTag()
    {
        _tagStore.AddTag("api", "backend");
        _tagStore.AddTag("api", "core");

        var cut = RenderEditor("api");
        cut.Find(".tag-add").Click();
        cut.Find(".tag-input").KeyDown("Backspace");

        Assert.Equal(["backend"], _tagStore.GetTags("api"));
    }

    [Fact]
    public void TagStoreChange_UpdatesChips()
    {
        var cut = RenderEditor("api");

        _tagStore.AddTag("api", "backend");

        cut.WaitForAssertion(() => Assert.Equal(["backend"], cut.FindAll(".tag-chip-name").Select(e => e.TextContent)));
    }

    private IRenderedComponent<ResourceTagEditor> RenderEditor(string resourceName) =>
        Render<ResourceTagEditor>(builder => builder.Add(p => p.ResourceName, resourceName));
}
