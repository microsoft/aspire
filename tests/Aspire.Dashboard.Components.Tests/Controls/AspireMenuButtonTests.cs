// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Bunit;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Aspire.Dashboard.Components.Tests.Controls;

public class AspireMenuButtonTests : DashboardTestContext
{
    [Fact]
    public void Disabled_DisablesButton()
    {
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);
        FluentUISetupHelpers.SetupFluentButton(this);
        FluentUISetupHelpers.SetupFluentMenu(this);

        var cut = RenderComponent<AspireMenuButton>(builder => builder
            .Add(component => component.MenuButtonId, "disabled-menu-button")
            .Add(component => component.ItemsProvider, () => [new MenuButtonItem { Text = "Item" }])
            .Add(component => component.Disabled, true));

        Assert.True(cut.FindComponent<FluentButton>().Instance.Disabled);
    }

    [Fact]
    public void Render_AddsCollapsedMenuPopupAria()
    {
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);
        FluentUISetupHelpers.SetupFluentButton(this);
        FluentUISetupHelpers.SetupFluentMenu(this);

        var cut = RenderComponent<AspireMenuButton>(builder =>
        {
            builder.Add(p => p.MenuButtonId, "view-options-button");
            builder.Add(p => p.Text, "View options");
            builder.Add(p => p.ItemsProvider, () => [new MenuButtonItem { Text = "Show hidden resources" }]);
        });

        var button = cut.Find("#view-options-button");

        Assert.Equal("menu", button.GetAttribute("aria-haspopup"));
        Assert.Equal("false", button.GetAttribute("aria-expanded"));
        Assert.False(button.HasAttribute("icon-only"));
    }

    [Fact]
    public void Render_WithoutText_AddsIconOnlyAttribute()
    {
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);
        FluentUISetupHelpers.SetupFluentButton(this);
        FluentUISetupHelpers.SetupFluentMenu(this);

        var cut = RenderComponent<AspireMenuButton>(builder =>
        {
            builder.Add(p => p.MenuButtonId, "icon-only-button");
            builder.Add(p => p.ItemsProvider, () => [new MenuButtonItem { Text = "Action" }]);
        });

        Assert.True(cut.Find("#icon-only-button").HasAttribute("icon-only"));
    }

    [Fact]
    public void Render_DefaultsIconColorToPrimaryAndAllowsOverride()
    {
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);
        FluentUISetupHelpers.SetupFluentButton(this);
        FluentUISetupHelpers.SetupFluentMenu(this);

        var cut = RenderComponent<AspireMenuButton>(builder =>
        {
            builder.Add(p => p.MenuButtonId, "icon-color-button");
            builder.Add(p => p.ItemsProvider, () => [new MenuButtonItem { Text = "Action" }]);
        });

        Assert.Equal(Color.Primary, cut.FindComponent<FluentIcon<Icon>>().Instance.Color);

        cut.SetParametersAndRender(builder => builder.Add(p => p.IconColor, Color.Default));

        Assert.Equal(Color.Default, cut.FindComponent<FluentIcon<Icon>>().Instance.Color);
    }

    [Fact]
    public void Render_WithoutTextAndWithStartIcon_SizesForBothIcons()
    {
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);
        FluentUISetupHelpers.SetupFluentButton(this);
        FluentUISetupHelpers.SetupFluentMenu(this);

        var cut = RenderComponent<AspireMenuButton>(builder =>
        {
            builder.Add(p => p.MenuButtonId, "icon-only-button");
            builder.Add(p => p.IconStart, new Icons.Regular.Size16.Delete());
            builder.Add(p => p.IconStartClass, "start-icon");
            builder.Add(p => p.IconStartColor, Color.Success);
            builder.Add(p => p.ItemsProvider, () => [new MenuButtonItem { Text = "Action" }]);
        });

        var button = cut.Find("#icon-only-button");
        Assert.False(button.HasAttribute("icon-only"));
        Assert.Collection(
            button.QuerySelectorAll("svg"),
            startIcon =>
            {
                Assert.Equal("start", startIcon.GetAttribute("slot"));
                Assert.Contains("start-icon", startIcon.ClassList);
                Assert.Contains("fill: var(--success)", startIcon.GetAttribute("style"), StringComparison.Ordinal);
            },
            menuIcon => Assert.Null(menuIcon.GetAttribute("slot")));
    }

    [Fact]
    public async Task ItemsProvider_AddsMenuWhenButtonIsClicked()
    {
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);
        FluentUISetupHelpers.SetupFluentButton(this);
        FluentUISetupHelpers.SetupFluentMenu(this);

        var providerInvocationCount = 0;
        var cut = RenderComponent<AspireMenuButton>(builder =>
        {
            builder.Add(p => p.MenuButtonId, "lazy-menu-button");
            builder.Add(p => p.Text, "View options");
            builder.Add(p => p.ItemsProvider, () =>
            {
                providerInvocationCount++;
                return [new MenuButtonItem { Text = $"Item {providerInvocationCount}" }];
            });
        });

        Assert.Equal(0, providerInvocationCount);
        Assert.Empty(cut.FindComponents<AspireMenu>());

        cut.Find("#lazy-menu-button").Click();

        Assert.Equal(1, providerInvocationCount);
        Assert.Single(cut.FindComponents<AspireMenu>());
        Assert.Single(cut.FindComponents<FluentMenu>());
        cut.WaitForAssertion(() => Assert.True(cut.FindComponent<AspireMenu>().Instance.Open));
        Assert.Equal("Item 1", cut.FindComponent<FluentMenuItem>().Instance.Label);

        var menu = cut.FindComponent<AspireMenu>().Instance;
        cut.Find("#lazy-menu-button").Click();

        Assert.Equal(1, providerInvocationCount);
        Assert.Same(menu, cut.FindComponent<AspireMenu>().Instance);
        Assert.False(cut.FindComponent<AspireMenu>().Instance.Open);

        cut.Find("#lazy-menu-button").Click();

        Assert.Equal(2, providerInvocationCount);
        Assert.Same(menu, cut.FindComponent<AspireMenu>().Instance);
        Assert.True(cut.FindComponent<AspireMenu>().Instance.Open);
        cut.WaitForAssertion(() => Assert.Equal("Item 2", cut.FindComponent<FluentMenuItem>().Instance.Label));

        var fluentMenu = cut.FindComponent<FluentMenu>();
        await fluentMenu.InvokeAsync(() => fluentMenu.Instance.OpenedChanged.InvokeAsync(false));

        Assert.False(cut.FindComponent<AspireMenu>().Instance.Open);
    }

    [Fact]
    public void ItemsProvider_RefreshesOpenMenuWhenParametersChange()
    {
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);
        FluentUISetupHelpers.SetupFluentButton(this);
        FluentUISetupHelpers.SetupFluentMenu(this);

        var itemText = "First item";
        Func<IList<MenuButtonItem>> itemsProvider = () => [new MenuButtonItem { Text = itemText }];
        var cut = RenderComponent<AspireMenuButton>(builder =>
        {
            builder.Add(p => p.MenuButtonId, "refresh-menu-button");
            builder.Add(p => p.Text, "View options");
            builder.Add(p => p.ItemsProvider, itemsProvider);
        });

        cut.Find("#refresh-menu-button").Click();
        cut.WaitForAssertion(() => Assert.Equal("First item", cut.FindComponent<FluentMenuItem>().Instance.Label));

        itemText = "Second item";
        cut.SetParametersAndRender(builder => builder.Add(p => p.Text, "Updated view options"));

        cut.WaitForAssertion(() => Assert.Equal("Second item", cut.FindComponent<FluentMenuItem>().Instance.Label));
    }

}
