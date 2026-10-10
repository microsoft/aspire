// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Dashboard.Utils;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Layout;

[UseCulture("en-US")]
public class MobileNavMenuTests : DashboardTestContext
{
    [Theory]
    [InlineData("/structuredlogs?logLevel=error")]
    [InlineData("/traces/detail/abc")]
    [InlineData("/metrics/resource/api")]
    public void Standalone_TelemetryPagesSelectTelemetryEntry(string path)
    {
        var cut = RenderMobileNavMenu(path, hasResourceService: false);

        AssertMenuItemIsActive(cut, Resources.Layout.NavMenuTelemetrySourcesTab);
        Assert.Equal(
            [Resources.Layout.NavMenuTelemetrySourcesTab, Resources.Layout.MainLayoutAspireRepoLink,
                Resources.Layout.MainLayoutAspireDashboardHelpLink, Resources.Layout.MainLayoutLaunchNotifications,
                Resources.Layout.MainLayoutLaunchSettings],
            cut.FindAll("fluent-menu-item").Select(item => item.GetAttribute("data-tooltip")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TerminalsNavigation_IsConditional(bool hasTerminals)
    {
        var cut = RenderMobileNavMenu("/terminals/resource/shell", hasResourceTerminals: hasTerminals);
        var titles = cut.FindAll("fluent-menu-item").Select(i => i.GetAttribute("data-tooltip")).Take(hasTerminals ? 9 : 8);
        var expected = new List<string>
        {
            Resources.Layout.NavMenuHomeTab,
            Resources.Layout.NavMenuResourcesTab,
            Resources.Layout.NavMenuTagsTab,
            Resources.Layout.NavMenuParametersTab,
            Resources.Layout.NavMenuGraphTab
        };
        if (hasTerminals)
        {
            expected.Add(Resources.Layout.NavMenuTerminalsTab);
        }
        expected.Add(Resources.Layout.NavMenuExtensionsTab);
        expected.Add(Resources.Layout.NavMenuTelemetrySourcesTab);
        expected.Add(Resources.Layout.MainLayoutAspireRepoLink);
        Assert.Equal(expected, titles);
        if (hasTerminals)
        {
            AssertMenuItemIsActive(cut, Resources.Layout.NavMenuTerminalsTab);
        }
    }

    [Fact]
    public void AppHost_TelemetryPaneSelectsTelemetryEntry()
    {
        var cut = RenderMobileNavMenu(DashboardUrls.TelemetrySourcesUrl());
        AssertMenuItemIsActive(cut, Resources.Layout.NavMenuTelemetrySourcesTab);
    }

    [Fact]
    public void Render_OpenMenu_TelemetryPageWithQueryStringSelectsResourcesEntry()
    {
        var cut = RenderMobileNavMenu(DashboardUrls.StructuredLogsUrl(logLevel: "warning"));

        AssertMenuItemIsActive(cut, Resources.Layout.NavMenuResourcesTab);
    }

    [Fact]
    public void Render_OpenMenu_TelemetryPageSelectsResourcesEntry()
    {
        var cut = RenderMobileNavMenu(DashboardUrls.StructuredLogsUrl());

        AssertMenuItemIsActive(cut, Resources.Layout.NavMenuResourcesTab);
    }

    [Fact]
    public void Render_OpenMenu_ResourceOverviewPageHasSemanticAndVisualSelectedState()
    {
        var cut = RenderMobileNavMenu(DashboardUrls.ResourceOverviewUrl("foo"));

        AssertMenuItemIsActive(cut, Resources.Layout.NavMenuResourcesTab);
    }

    [Fact]
    public void Render_OpenMenu_HomePageHasSemanticAndVisualSelectedState()
    {
        var cut = RenderMobileNavMenu(DashboardUrls.HomeUrl());

        AssertMenuItemIsActive(cut, Resources.Layout.NavMenuHomeTab);
    }

    [Fact]
    public void Render_OpenMenu_ParametersPageHasSemanticAndVisualSelectedState()
    {
        var cut = RenderMobileNavMenu(DashboardUrls.ParametersUrl(hiddenStates: "Running"));

        AssertMenuItemIsActive(cut, Resources.Layout.NavMenuParametersTab);
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

        Assert.Contains("max-height: calc(100dvh - var(--mobile-header-height) - var(--mobile-nav-menu-offset))", style);
        Assert.DoesNotContain("height: 100vh", style);
        Assert.Contains("margin-top: var(--mobile-nav-menu-offset)", style);
        Assert.Contains("overflow-y: auto", style);
        Assert.Contains("padding-block: var(--mobile-nav-menu-focus-padding)", style);
        Assert.Contains("scroll-padding-block: var(--mobile-nav-menu-focus-padding)", style);
        Assert.Contains("mobile-nav-menu", menu.ClassList);
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
