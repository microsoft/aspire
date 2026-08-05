// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

public class AspireMenuButtonTests : DashboardTestContext
{
    [Fact]
    public void Render_AddsMenuPopupAriaWithoutExpandedState()
    {
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);
        FluentUISetupHelpers.SetupFluentButton(this);
        FluentUISetupHelpers.SetupFluentMenu(this);

        RenderComponent<FluentMenuProvider>();
        var cut = RenderComponent<AspireMenuButton>(builder =>
        {
            builder.Add(p => p.MenuButtonId, "view-options-button");
            builder.Add(p => p.Text, "View options");
            builder.Add(p => p.ItemsProvider, () => [new MenuButtonItem { Text = "Show hidden resources" }]);
        });

        var button = cut.Find("#view-options-button");

        Assert.Equal("menu", button.GetAttribute("aria-haspopup"));
        Assert.False(button.HasAttribute("aria-expanded"));
    }

    [Fact]
    public async Task ItemsProvider_AddsMenuWhenButtonIsClicked()
    {
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);
        FluentUISetupHelpers.SetupFluentButton(this);
        FluentUISetupHelpers.SetupFluentMenu(this);

        var providerInvocationCount = 0;
        var provider = RenderComponent<FluentMenuProvider>();
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
        var prepareInvocation = Assert.Single(JSInterop.Invocations, invocation => invocation.Identifier == "prepareForFluentMenuInitialization");
        var waitInvocation = Assert.Single(JSInterop.Invocations, invocation => invocation.Identifier == "waitForFluentMenuInitialization");
        Assert.Equal(100, waitInvocation.Arguments[1]);
        var invocations = JSInterop.Invocations.ToList();
        Assert.True(invocations.IndexOf(prepareInvocation) < invocations.IndexOf(waitInvocation));
        Assert.Single(cut.FindComponents<AspireMenu>());
        Assert.Single(cut.FindComponents<FluentMenu>());
        cut.WaitForAssertion(() => Assert.True(cut.FindComponent<AspireMenu>().Instance.Open));
        var menuService = Services.GetRequiredService<IMenuService>();
        var registeredMenu = menuService.Menus.Single();
        Assert.True(registeredMenu.OpenChanged.HasDelegate);
        provider.WaitForAssertion(() =>
        {
            Assert.True(registeredMenu.Open);
            Assert.Single(provider.FindComponents<FluentMenu>());
        });
        Assert.True(provider.FindComponent<FluentMenu>().Instance.Open);
        Assert.Equal("Item 1", provider.FindComponent<FluentMenuItem>().Instance.Label);

        var menu = cut.FindComponent<AspireMenu>().Instance;
        cut.Find("#lazy-menu-button").Click();

        Assert.Equal(1, providerInvocationCount);
        Assert.Same(menu, cut.FindComponent<AspireMenu>().Instance);
        Assert.False(Services.GetRequiredService<IMenuService>().Menus.Single().Open);

        cut.Find("#lazy-menu-button").Click();

        Assert.Equal(2, providerInvocationCount);
        Assert.Same(menu, cut.FindComponent<AspireMenu>().Instance);
        Assert.True(registeredMenu.Open);
        provider.WaitForAssertion(() => Assert.Equal("Item 2", provider.FindComponent<FluentMenuItem>().Instance.Label));

        await cut.InvokeAsync(provider.FindComponent<FluentMenu>().Instance.CloseAsync);

        Assert.False(cut.FindComponent<AspireMenu>().Instance.Open);
        Assert.False(registeredMenu.Open);
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
        var provider = RenderComponent<FluentMenuProvider>();
        var cut = RenderComponent<AspireMenuButton>(builder =>
        {
            builder.Add(p => p.MenuButtonId, "refresh-menu-button");
            builder.Add(p => p.Text, "View options");
            builder.Add(p => p.ItemsProvider, itemsProvider);
        });

        cut.Find("#refresh-menu-button").Click();
        provider.WaitForAssertion(() => Assert.Equal("First item", provider.FindComponent<FluentMenuItem>().Instance.Label));

        itemText = "Second item";
        cut.SetParametersAndRender(builder => builder.Add(p => p.Text, "Updated view options"));

        provider.WaitForAssertion(() => Assert.Equal("Second item", provider.FindComponent<FluentMenuItem>().Instance.Label));
    }

    [Fact]
    public void ItemsProvider_DoesNotRefreshOpenMenuWhenItemsAreUnchanged()
    {
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);
        FluentUISetupHelpers.SetupFluentButton(this);
        FluentUISetupHelpers.SetupFluentMenu(this);

        var providerInvocationCount = 0;
        var provider = RenderComponent<FluentMenuProvider>();
        var cut = RenderComponent<AspireMenuButton>(builder =>
        {
            builder.Add(p => p.MenuButtonId, "stable-menu-button");
            builder.Add(p => p.Text, "View options");
            builder.Add(p => p.ItemsProvider, () =>
            {
                providerInvocationCount++;
                return [new MenuButtonItem { Text = "Item" }];
            });
        });

        cut.Find("#stable-menu-button").Click();
        provider.WaitForAssertion(() => Assert.Single(provider.FindComponents<FluentMenuItem>()));
        var initialItemId = provider.FindComponent<FluentMenuItem>().Instance.Id;
        var menu = cut.FindComponent<AspireMenu>();
        var initialMenuRenderCount = menu.RenderCount;

        for (var i = 0; i < 5; i++)
        {
            cut.SetParametersAndRender(builder => builder.Add(p => p.Text, $"View options {i}"));
        }

        Assert.Equal(6, providerInvocationCount);
        Assert.Equal(initialItemId, provider.FindComponent<FluentMenuItem>().Instance.Id);
        Assert.Equal(initialMenuRenderCount + 5, menu.RenderCount);
    }

    [Fact]
    public async Task DisposeAsync_CancelsPendingMenuInitialization()
    {
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);
        FluentUISetupHelpers.SetupFluentButton(this);
        FluentUISetupHelpers.SetupFluentMenu(this);
        var initialization = JSInterop.SetupVoid("waitForFluentMenuInitialization", "pending-menu-button", 100);
        JSInterop.SetupVoid("cancelFluentMenuInitialization", "pending-menu-button").SetVoidResult();
        var cut = RenderComponent<AspireMenuButton>(builder =>
        {
            builder.Add(p => p.MenuButtonId, "pending-menu-button");
            builder.Add(p => p.ItemsProvider, () => [new MenuButtonItem { Text = "Item" }]);
        });

        cut.Find("#pending-menu-button").Click();
        await cut.Instance.DisposeAsync();

        var cancellationInvocation = Assert.Single(JSInterop.Invocations, invocation => invocation.Identifier == "cancelFluentMenuInitialization");
        Assert.Equal("pending-menu-button", cancellationInvocation.Arguments[0]);
        initialization.SetVoidResult();
    }

}
