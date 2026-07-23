// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

public class AspireMenuTests : DashboardTestContext
{
    [Fact]
    public async Task DisposeAsync_RemovesFluentMenuFromMenuProvider()
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        FluentUISetupHelpers.SetupFluentMenu(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);

        var menuService = Services.GetRequiredService<IMenuService>();
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
        var menu = menuHost.FindComponent<FluentMenu>().Instance;
        Assert.Contains(menu, menuService.Menus);

        await menuHost.InvokeAsync(() => menuService.RefreshMenuAsync(menu.Id!, isOpen: true));

        provider.WaitForAssertion(() => Assert.Single(provider.FindComponents<FluentMenu>()));

        menuHost.SetParametersAndRender(builder =>
        {
            builder.Add(p => p.Value, false);
            builder.Add(p => p.ChildContent, (RenderFragment)(_ => { }));
        });

        Assert.Empty(menuService.Menus);
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

        var cut = Render(builder =>
        {
            builder.OpenComponent<FluentMenuProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AspireMenuButton>(1);
            builder.AddAttribute(2, nameof(AspireMenuButton.MenuButtonId), anchor);
            builder.AddAttribute(3, nameof(AspireMenuButton.Title), "View options");
            builder.AddAttribute(4, nameof(AspireMenuButton.Items), items);
            builder.CloseComponent();
        });

        cut.Find($"#{anchor}").Click();
        cut.WaitForElement("fluent-menu-item").Click();

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

        var cut = Render(builder =>
        {
            builder.OpenComponent<FluentMenuProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AspireMenuButton>(1);
            builder.AddAttribute(2, nameof(AspireMenuButton.MenuButtonId), anchor);
            builder.AddAttribute(3, nameof(AspireMenuButton.Title), "View options");
            builder.AddAttribute(4, nameof(AspireMenuButton.Items), items);
            builder.AddAttribute(5, nameof(AspireMenuButton.RestoreFocusOnItemClick), false);
            builder.CloseComponent();
        });

        cut.Find($"#{anchor}").Click();
        cut.WaitForElement("fluent-menu-item").Click();

        Assert.True(itemClicked);
        var focusElementInvocations = JSInterop.Invocations
            .Where(invocation => invocation.Identifier == "focusElement")
            .ToArray();
        Assert.Empty(focusElementInvocations);
    }

}
