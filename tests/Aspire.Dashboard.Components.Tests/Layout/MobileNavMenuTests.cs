// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Tests.Shared;
using Bunit;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using Xunit;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Aspire.Dashboard.Components.Tests.Layout;

[UseCulture("en-US")]
public class MobileNavMenuTests : DashboardTestContext
{
    [Fact]
    public void WithoutPageEntries_ShowsOnlyHeaderActions()
    {
        var cut = RenderMobileNavMenu();

        Assert.Equal(
            [Resources.Layout.MainLayoutHelp, Resources.Layout.MainLayoutLaunchNotifications, Resources.Layout.MainLayoutLaunchSettings],
            cut.FindAll("fluent-menu-item").Select(item => item.GetAttribute("data-tooltip")));
        Assert.Empty(cut.FindAll("""fluent-menu-item[aria-current="page"]"""));
    }

    [Fact]
    public void MobileNavMenu_ConstrainedToRemainingViewport()
    {
        var cut = RenderMobileNavMenu();

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
        _ = RenderMobileNavMenu();

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
        var cut = RenderMobileNavMenu(() => closeNavMenuCalled = true, isNavMenuOpen: false);

        await cut.InvokeAsync(cut.Instance.CloseMobileNavMenuFromFocusLossAsync);

        Assert.True(closeNavMenuCalled);
        Assert.DoesNotContain(JSInterop.Invocations, invocation => invocation.Identifier == "focusElement");
    }

    [Fact]
    public async Task CloseMobileNavMenuFromKeyboardAsync_ClosesMenuAndRestoresFocus()
    {
        JSInterop.SetupVoid("focusElement", _ => true).SetVoidResult();
        var closeNavMenuCalled = false;
        var cut = RenderMobileNavMenu(() => closeNavMenuCalled = true, isNavMenuOpen: false);

        await cut.InvokeAsync(cut.Instance.CloseMobileNavMenuFromKeyboardAsync);

        Assert.True(closeNavMenuCalled);
        var invocation = Assert.Single(JSInterop.Invocations, invocation => invocation.Identifier == "focusElement");
        var argument = Assert.Single(invocation.Arguments);
        Assert.Equal(MainLayout.NavigationButtonId, argument);
    }

    [Fact]
    public async Task PageEntries_AreListedFirstWithTheirActiveStateAndErrorCount()
    {
        var menuClosed = false;
        var overviewClicked = false;
        IEnumerable<MobileNavMenuEntry> pageEntries =
        [
            new("home", "Home", () => Task.CompletedTask, new Icons.Regular.Size24.Home(), ActiveIcon: new Icons.Filled.Size24.Home()),
            new("overview", "Overview", () => { overviewClicked = true; return Task.CompletedTask; }, new Icons.Regular.Size24.Board()),
            new("logs", "Structured logs", () => Task.CompletedTask, new Icons.Regular.Size24.SlideTextSparkle(), ActiveIcon: new Icons.Filled.Size24.SlideTextSparkle(),
                IsActive: true, ErrorCount: 3)
        ];
        var cut = RenderMobileNavMenu(() => menuClosed = true, pageEntries: () => pageEntries);

        Assert.Equal(
            ["Home", "Overview", "Structured logs", Resources.Layout.MainLayoutHelp],
            cut.FindAll("fluent-menu-item").Select(item => item.GetAttribute("data-tooltip")).Take(4));
        AssertMenuItemIsActive(cut, "Structured logs");
        Assert.Equal("3", Assert.Single(cut.FindAll(".mobile-nav-menu-error-count")).TextContent.Trim());

        var overviewItem = cut.FindAll("fluent-menu-item").Single(item => item.GetAttribute("data-tooltip") == "Overview");
        await cut.InvokeAsync(() => overviewItem.TriggerEvent("onmenuitemchange",
            new MenuItemEventArgs { Id = overviewItem.Id, Text = overviewItem.TextContent }));

        Assert.True(menuClosed);
        Assert.True(overviewClicked);
    }

    private IRenderedComponent<MobileNavMenu> RenderMobileNavMenu(Action? closeNavMenu = null, bool isNavMenuOpen = true, Func<IEnumerable<MobileNavMenuEntry>>? pageEntries = null)
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        FluentUISetupHelpers.SetupFluentMenu(this);
        FluentUISetupHelpers.SetupFluentDivider(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);
        LayoutSetupHelpers.SetupMobileNavMenuKeyboardNavigation(this);

        return Render<MobileNavMenu>(builder =>
        {
            builder.Add(p => p.IsNavMenuOpen, isNavMenuOpen);
            builder.Add(p => p.CloseNavMenu, closeNavMenu ?? (() => { }));
            builder.Add(p => p.LaunchHelpAsync, () => Task.CompletedTask);
            builder.Add(p => p.LaunchNotificationsAsync, () => Task.CompletedTask);
            builder.Add(p => p.LaunchSettingsAsync, () => Task.CompletedTask);
            builder.Add(p => p.PageEntries, pageEntries);
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
