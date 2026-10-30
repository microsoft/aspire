// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Dashboard.Utils;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Layout;

[UseCulture("en-US")]
public class MobileNavMenuTests : DashboardTestContext
{
    [Fact]
    public void Standalone_ShowsUtilityEntries()
    {
        var cut = RenderMobileNavMenu("/structuredlogs?logLevel=error", hasResourceService: false);

        Assert.Equal(
            [Resources.Layout.MainLayoutAspireRepoLink, Resources.Layout.MainLayoutKeyboardShortcuts,
                Resources.Layout.MainLayoutAspireDashboardHelpLink, Resources.Layout.MainLayoutLaunchNotifications,
                Resources.Layout.MainLayoutLaunchSettings],
            cut.FindAll("fluent-menu-item").Select(item => item.GetAttribute("data-tooltip")));
        Assert.Empty(cut.FindAll("""fluent-menu-item[aria-current="page"]"""));
    }

    [Fact]
    public async Task Documentation_OpensDashboardDocsInNewTabAndClosesMenu()
    {
        JSInterop.SetupVoid("open", _ => true).SetVoidResult();
        var menuClosed = false;
        var cut = RenderMobileNavMenu(DashboardUrls.HomeUrl(), () => menuClosed = true);
        var documentationItem = cut.FindAll("fluent-menu-item")
            .Single(item => item.GetAttribute("data-tooltip") == Resources.Layout.MainLayoutAspireDashboardHelpLink);

        await cut.InvokeAsync(() => documentationItem.TriggerEvent("onmenuitemchange",
            new MenuItemEventArgs { Id = documentationItem.Id, Text = documentationItem.TextContent }));

        Assert.True(menuClosed);
        var invocation = Assert.Single(JSInterop.Invocations, invocation => invocation.Identifier == "open");
        Assert.Equal(["https://aspire.dev/dashboard/overview/", "_blank", "noopener,noreferrer"], invocation.Arguments);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TerminalsNavigation_IsConditional(bool hasTerminals)
    {
        var cut = RenderMobileNavMenu("/terminals/resource/shell", hasResourceTerminals: hasTerminals);
        var titles = cut.FindAll("fluent-menu-item").Select(i => i.GetAttribute("data-tooltip")).Take(hasTerminals ? 5 : 4);
        var expected = new List<string>
        {
            Resources.Layout.MainLayoutAspireRepoLink,
            Resources.Layout.MainLayoutKeyboardShortcuts,
            Resources.Layout.MainLayoutAspireDashboardHelpLink
        };
        if (hasTerminals)
        {
            expected.Insert(0, Resources.Layout.NavMenuTerminalsTab);
        }
        expected.Add(Resources.Layout.MainLayoutLaunchNotifications);
        Assert.Equal(expected, titles);
        if (hasTerminals)
        {
            AssertMenuItemIsActive(cut, Resources.Layout.NavMenuTerminalsTab);
        }
    }

    [Fact]
    public void MobileNavMenu_ConstrainedToRemainingViewport()
    {
        var cut = RenderMobileNavMenu(DashboardUrls.HomeUrl());

        var menu = cut.Find("fluent-menu-list");
        var style = menu.GetAttribute("style");

        Assert.Empty(cut.FindAll("fluent-menu"));
        Assert.Equal(MobileNavMenu.MobileNavMenuId, menu.Id);
        Assert.Equal(cut.FindAll("fluent-menu-item").Count, menu.QuerySelectorAll(":scope > fluent-menu-item").Length);
        Assert.Equal(cut.FindAll("fluent-menu-item").Count - 1, cut.FindAll("fluent-divider").Count);
        Assert.Equal("fluent-menu-item", menu.Children[0].LocalName);
        Assert.Equal("fluent-menu-item", menu.Children[menu.Children.Length - 1].LocalName);

        Assert.Contains("max-height: calc(100dvh - var(--mobile-header-height))", style);
        Assert.DoesNotContain("height: 100vh", style);
        Assert.DoesNotContain("margin-top", style);
        Assert.Contains("overflow-y: auto", style);
        Assert.Contains("padding-block: var(--mobile-nav-menu-focus-padding)", style);
        Assert.Contains("scroll-padding-block: var(--mobile-nav-menu-focus-padding)", style);
        Assert.Contains("mobile-nav-menu", menu.ClassList);
        Assert.Single(cut.FindAll(".mobile-nav-scrim"));
    }

    [Fact]
    public void Render_OpenMenu_InitializesKeyboardNavigationWithComponentReferenceAndMenuId()
    {
        _ = RenderMobileNavMenu(DashboardUrls.HomeUrl());

        var invocation = Assert.Single(JSInterop.Invocations, i => i.Identifier == "initializeMobileNavMenuKeyboardNavigation");
        Assert.Collection(
            invocation.Arguments,
            argument => Assert.IsAssignableFrom<DotNetObjectReference<MobileNavMenu>>(argument),
            argument => Assert.Equal(MobileNavMenu.MobileNavMenuId, argument));
    }

    [Fact]
    public async Task CloseMobileNavMenuFromFocusLossAsync_ClosesMenuWithoutRestoringFocus()
    {
        var closeNavMenuCalled = false;
        var cut = RenderMobileNavMenu(DashboardUrls.HomeUrl(), () => closeNavMenuCalled = true, isNavMenuOpen: false);

        await cut.InvokeAsync(cut.Instance.CloseMobileNavMenuFromFocusLossAsync);

        Assert.True(closeNavMenuCalled);
        Assert.DoesNotContain(JSInterop.Invocations, invocation => invocation.Identifier == "focusElement");
    }

    [Fact]
    public async Task CloseMobileNavMenuFromKeyboardAsync_ClosesMenuAndRestoresFocus()
    {
        JSInterop.SetupVoid("focusElement", _ => true).SetVoidResult();
        var closeNavMenuCalled = false;
        var cut = RenderMobileNavMenu(DashboardUrls.HomeUrl(), () => closeNavMenuCalled = true, isNavMenuOpen: false);

        await cut.InvokeAsync(cut.Instance.CloseMobileNavMenuFromKeyboardAsync);

        Assert.True(closeNavMenuCalled);
        var invocation = Assert.Single(JSInterop.Invocations, invocation => invocation.Identifier == "focusElement");
        var argument = Assert.Single(invocation.Arguments);
        Assert.Equal(MainLayout.NavigationButtonId, argument);
    }

    private IRenderedComponent<MobileNavMenu> RenderMobileNavMenu(string currentUrl, Action? closeNavMenu = null, bool isNavMenuOpen = true, bool hasResourceTerminals = false, bool hasResourceService = true)
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        Services.AddSingleton<IDashboardClient>(new TestDashboardClient(isEnabled: hasResourceService));
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        FluentUISetupHelpers.SetupFluentMenu(this);
        FluentUISetupHelpers.SetupFluentDivider(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);
        LayoutSetupHelpers.SetupMobileNavMenuKeyboardNavigation(this);

        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo(currentUrl);

        return Render<MobileNavMenu>(builder =>
        {
            builder.Add(p => p.IsNavMenuOpen, isNavMenuOpen);
            builder.Add(p => p.HasResourceTerminals, hasResourceTerminals);
            builder.Add(p => p.CloseNavMenu, closeNavMenu ?? (() => { }));
            builder.Add(p => p.LaunchHelpAsync, () => Task.CompletedTask);
            builder.Add(p => p.LaunchAIAgentsAsync, () => Task.CompletedTask);
            builder.Add(p => p.IsAgentHelpEnabled, false);
            builder.Add(p => p.LaunchNotificationsAsync, () => Task.CompletedTask);
            builder.Add(p => p.LaunchSettingsAsync, () => Task.CompletedTask);
        });
    }

    private static void AssertMenuItemIsActive(IRenderedComponent<MobileNavMenu> cut, string expectedText)
    {
        var currentItem = Assert.Single(cut.FindAll("""fluent-menu-item[aria-current="page"]"""));

        Assert.Contains(expectedText, currentItem.TextContent);
        Assert.True(currentItem.ClassList.Contains("mobile-nav-menu-item-active"));

        // The active item swaps to the filled icon variant and tags the slot wrapper
        // so the selected state has a non-color cue.
        var activeIconSlot = Assert.Single(currentItem.QuerySelectorAll(".mobile-nav-menu-icon-active"));
        Assert.Equal("start", activeIconSlot.GetAttribute("slot"));
        Assert.NotEmpty(activeIconSlot.QuerySelectorAll("svg"));

        var inactiveItems = cut.FindAll("fluent-menu-item")
            .Where(item => item.GetAttribute("aria-current") != "page")
            .ToList();
        Assert.NotEmpty(inactiveItems);
        Assert.All(inactiveItems, item =>
        {
            Assert.False(item.ClassList.Contains("mobile-nav-menu-item-active"));
            Assert.Empty(item.QuerySelectorAll(".mobile-nav-menu-icon-active"));
        });
    }
}
