// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

public class AspireMenuTests : DashboardTestContext
{
    [Fact]
    public void UnanchoredAspireMenu_RendersFluentMenu()
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        FluentUISetupHelpers.SetupFluentMenu(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);

        var menuHost = RenderComponent<AspireMenu>(builder =>
        {
            builder.Add(p => p.Anchor, "menu-anchor");
            builder.Add(p => p.Anchored, false);
            builder.Add(p => p.Items, new[] { new MenuButtonItem { Text = "Item" } });
        });

        var menu = Assert.Single(menuHost.FindComponents<FluentMenu>()).Instance;
        Assert.Null(menu.Trigger);
        Assert.Single(menuHost.FindComponents<FluentMenuList>());
    }

    [Fact]
    public void NestedAspireMenu_RendersItemsDirectlyInSubmenu()
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        FluentUISetupHelpers.SetupFluentMenu(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);

        var menuHost = RenderComponent<AspireMenu>(builder =>
        {
            builder.Add(p => p.Anchor, "menu-anchor");
            builder.Add(p => p.Items, new[]
            {
                new MenuButtonItem
                {
                    Text = "Commands",
                    NestedMenuItems =
                    [
                        new MenuButtonItem { Text = "Start" },
                        new MenuButtonItem { Text = "Stop" }
                    ]
                }
            });
        });

        Assert.Empty(menuHost.FindAll("fluent-menu-item > fluent-menu-list[slot='submenu'] > fluent-menu-list"));
        var nestedItems = menuHost.FindAll("fluent-menu-item > fluent-menu-list[slot='submenu'] > fluent-menu-item");
        Assert.Collection(
            nestedItems,
            item => Assert.Equal("Start", item.TextContent.Trim()),
            item => Assert.Equal("Stop", item.TextContent.Trim()));
    }

    [Fact]
    public async Task RemoveAspireMenu_RemovesFluentMenuFromHost()
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        FluentUISetupHelpers.SetupFluentMenu(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);

        var menuHost = RenderComponent<CascadingValue<bool>>(builder =>
        {
            builder.Add(p => p.Value, false);
            builder.AddChildContent<AspireMenu>(menuBuilder =>
            {
                menuBuilder.Add(p => p.Anchor, "menu-anchor");
                menuBuilder.Add(p => p.Items, new[] { new MenuButtonItem { Text = "Item" } });
            });
        });
        Assert.Single(menuHost.FindComponents<FluentMenu>());

        await menuHost.InvokeAsync(() => menuHost.FindComponent<AspireMenu>().Instance.OpenAsync(1920, 1080, 10, 10));

        menuHost.SetParametersAndRender(builder =>
        {
            builder.Add(p => p.Value, false);
            builder.Add(p => p.ChildContent, (RenderFragment)(_ => { }));
        });

        menuHost.WaitForAssertion(() => Assert.Empty(menuHost.FindComponents<FluentMenu>()));
    }

    [Fact]
    public async Task ClickItem_MenuButton_FocusesAnchorBeforeOnClick()
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        FluentUISetupHelpers.SetupFluentMenu(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);
        FluentUISetupHelpers.SetupFluentButton(this);

        var anchor = "view-options-button";
        var itemClicked = false;
        var focusElementInvocationHandler = JSInterop.SetupVoid("focusElement", anchor);
        focusElementInvocationHandler.SetVoidResult();
        var items = new List<MenuButtonItem>
        {
            new()
            {
                Text = "Show hidden resources",
                OnClick = () =>
                {
                    Assert.Single(focusElementInvocationHandler.Invocations);
                    itemClicked = true;

                    return Task.CompletedTask;
                }
            }
        };

        var menuButton = RenderComponent<AspireMenuButton>(builder =>
        {
            builder.Add(p => p.MenuButtonId, anchor);
            builder.Add(p => p.Title, "View options");
            builder.Add(p => p.ItemsProvider, () => items);
        });

        menuButton.Find($"#{anchor}").Click();
        var menuItem = menuButton.FindComponent<FluentMenuItem>();
        await menuButton.InvokeAsync(() => menuItem.Instance.OnClick.InvokeAsync(new MenuItemEventArgs()));

        Assert.True(itemClicked);
        var invocation = Assert.Single(focusElementInvocationHandler.Invocations);
        Assert.Collection(invocation.Arguments,
            argument => Assert.Equal(anchor, Assert.IsType<string>(argument)));
    }

    [Fact]
    public async Task ClickItem_MenuButtonWithFocusRestorationDisabled_DoesNotFocusAnchor()
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        FluentUISetupHelpers.SetupFluentMenu(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);
        FluentUISetupHelpers.SetupFluentButton(this);

        var anchor = "view-options-button";
        var itemClicked = false;
        var items = new List<MenuButtonItem>
        {
            new()
            {
                Text = "Show hidden resources",
                OnClick = () =>
                {
                    itemClicked = true;
                    return Task.CompletedTask;
                }
            }
        };

        var menuButton = RenderComponent<AspireMenuButton>(builder =>
        {
            builder.Add(p => p.MenuButtonId, anchor);
            builder.Add(p => p.Title, "View options");
            builder.Add(p => p.ItemsProvider, () => items);
            builder.Add(p => p.RestoreFocusOnItemClick, false);
        });

        menuButton.Find($"#{anchor}").Click();
        var menuItem = menuButton.FindComponent<FluentMenuItem>();
        await menuButton.InvokeAsync(() => menuItem.Instance.OnClick.InvokeAsync(new MenuItemEventArgs()));

        Assert.True(itemClicked);
        var focusElementInvocations = JSInterop.Invocations
            .Where(invocation => invocation.Identifier == "focusElement")
            .ToArray();
        Assert.Empty(focusElementInvocations);
    }

    [Fact]
    public void CheckableItems_RenderAccessibleRoleAndCheckedStateInDom()
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        FluentUISetupHelpers.SetupFluentMenu(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);
        FluentUISetupHelpers.SetupFluentButton(this);

        var anchor = "view-options-button";
        var items = new List<MenuButtonItem>
        {
            new() { Text = "Console", Role = MenuItemRole.Checkbox, Checked = false },
            new() { Text = "Terminal", Role = MenuItemRole.Checkbox, Checked = true },
        };

        var menuButton = RenderComponent<AspireMenuButton>(builder =>
        {
            builder.Add(p => p.MenuButtonId, anchor);
            builder.Add(p => p.Title, "View options");
            builder.Add(p => p.ItemsProvider, () => items);
        });

        menuButton.Find($"#{anchor}").Click();
        menuButton.WaitForElement("fluent-menu-item");

        var menuItems = menuButton.FindAll("fluent-menu-item");
        Assert.Equal(2, menuItems.Count);

        // Both options must carry the checkable role so assistive technology announces
        // them as a selectable set. Asserting on the rendered element (not the backing
        // MenuButtonItem) guards the Role passthrough through AspireMenu -> FluentMenuItem:
        // the unchecked item only gets role="menuitemcheckbox" from an explicit Role, since
        // FluentMenuItem otherwise infers that role solely from a checked item.
        Assert.Equal("menuitemcheckbox", menuItems[0].GetAttribute("role"));
        Assert.Equal("menuitemcheckbox", menuItems[1].GetAttribute("role"));

        // Only the active option reflects the checked state in the DOM. This guards the
        // Checked passthrough; without it the rendered items would lose their checked state.
        Assert.False(menuItems[0].HasAttribute("checked"));
        Assert.True(menuItems[1].HasAttribute("checked"));
    }
}
