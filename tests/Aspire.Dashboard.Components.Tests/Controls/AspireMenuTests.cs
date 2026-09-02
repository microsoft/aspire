// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Aspire.Dashboard.Components.Tests.Controls;

public class AspireMenuTests : DashboardTestContext
{
    [Fact]
    public async Task ClickSecondaryAction_RefreshesOpenMenu()
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        FluentUISetupHelpers.SetupFluentMenu(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);
        FluentUISetupHelpers.SetupFluentButton(this);

        var item = new MenuButtonItem
        {
            Text = "Historical run",
            SecondaryActionIcon = new Icons.Regular.Size16.Pin(),
            SecondaryActionAriaLabel = "Pin run"
        };
        item.OnSecondaryActionClick = () =>
        {
            item.SecondaryActionIcon = new Icons.Filled.Size16.Pin();
            item.SecondaryActionAriaLabel = "Unpin run";
            item.IsSecondaryActionSelected = true;
            return Task.CompletedTask;
        };

        var menuService = Services.GetRequiredService<IMenuService>();
        var provider = RenderComponent<FluentMenuProvider>();
        var menuHost = RenderComponent<CascadingValue<bool>>(builder =>
        {
            builder.Add(p => p.Value, false);
            builder.AddChildContent<AspireMenu>(menuBuilder =>
            {
                menuBuilder.Add(p => p.Anchor, "menu-anchor");
                menuBuilder.Add(p => p.Open, true);
                menuBuilder.Add(p => p.Items, new[] { item });
            });
        });
        var menu = menuHost.FindComponent<FluentMenu>().Instance;
        await menuHost.InvokeAsync(() => menuService.RefreshMenuAsync(menu.Id!, isOpen: true));

        var pinButton = provider.WaitForElement("fluent-button[aria-label='Pin run']");
        Assert.Equal("false", pinButton.GetAttribute("aria-pressed"));
        pinButton.Click();

        provider.WaitForAssertion(() =>
        {
            Assert.Single(provider.FindComponents<FluentMenu>());
            var unpinButton = provider.Find("fluent-button[aria-label='Unpin run']");
            Assert.Equal("true", unpinButton.GetAttribute("aria-pressed"));
        });
    }

    [Fact]
    public void UnanchoredAspireMenu_RendersFluentMenu()
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        FluentUISetupHelpers.SetupFluentMenu(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);

        var provider = RenderComponent<FluentMenuProvider>();
        var menuHost = RenderComponent<AspireMenu>(builder =>
        {
            builder.Add(p => p.Anchor, "menu-anchor");
            builder.Add(p => p.Anchored, false);
            builder.Add(p => p.Items, new[] { new MenuButtonItem { Text = "Item" } });
        });

        var menu = Assert.Single(menuHost.FindComponents<FluentMenu>()).Instance;
        Assert.False(menu.Anchored);
        Assert.Empty(provider.FindComponents<FluentMenu>());
    }

    [Fact]
    public async Task RemoveAspireMenu_UnregistersFluentMenuFromMenuProvider()
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        FluentUISetupHelpers.SetupFluentMenu(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);

        var provider = RenderComponent<FluentMenuProvider>();
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

        provider.WaitForAssertion(() => Assert.Single(provider.FindComponents<FluentMenu>()));

        menuHost.SetParametersAndRender(builder =>
        {
            builder.Add(p => p.Value, false);
            builder.Add(p => p.ChildContent, (RenderFragment)(_ => { }));
        });

        provider.WaitForAssertion(() => Assert.Empty(provider.FindComponents<FluentMenu>()));
    }

    [Fact]
    public void ClickItem_MenuButton_FocusesAnchorBeforeOnClick()
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

        var provider = RenderComponent<FluentMenuProvider>();
        var menuButton = RenderComponent<AspireMenuButton>(builder =>
        {
            builder.Add(p => p.MenuButtonId, anchor);
            builder.Add(p => p.Title, "View options");
            builder.Add(p => p.ItemsProvider, () => items);
        });

        menuButton.Find($"#{anchor}").Click();
        provider.WaitForElement("fluent-menu-item").Click();

        Assert.True(itemClicked);
        var invocation = Assert.Single(focusElementInvocationHandler.Invocations);
        Assert.Collection(invocation.Arguments,
            argument => Assert.Equal(anchor, Assert.IsType<string>(argument)));
    }

    [Fact]
    public void ClickItem_MenuButtonWithFocusRestorationDisabled_DoesNotFocusAnchor()
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

        var provider = RenderComponent<FluentMenuProvider>();
        var menuButton = RenderComponent<AspireMenuButton>(builder =>
        {
            builder.Add(p => p.MenuButtonId, anchor);
            builder.Add(p => p.Title, "View options");
            builder.Add(p => p.ItemsProvider, () => items);
            builder.Add(p => p.RestoreFocusOnItemClick, false);
        });

        menuButton.Find($"#{anchor}").Click();
        provider.WaitForElement("fluent-menu-item").Click();

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
            new() { Text = "Console", Role = MenuItemRole.MenuItemCheckbox, Checked = false },
            new() { Text = "Terminal", Role = MenuItemRole.MenuItemCheckbox, Checked = true },
        };

        var provider = RenderComponent<FluentMenuProvider>();
        var menuButton = RenderComponent<AspireMenuButton>(builder =>
        {
            builder.Add(p => p.MenuButtonId, anchor);
            builder.Add(p => p.Title, "View options");
            builder.Add(p => p.ItemsProvider, () => items);
        });

        menuButton.Find($"#{anchor}").Click();
        provider.WaitForElement("fluent-menu-item");

        var menuItems = provider.FindAll("fluent-menu-item");
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
