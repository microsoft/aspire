// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Dashboard.Utils;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Layout;

[UseCulture("en-US")]
public class MobileNavMenuTests : DashboardTestContext
{
    [Fact]
    public void Render_OpenMenu_CurrentPageHasSemanticAndVisualSelectedState()
    {
        var cut = RenderMobileNavMenu(DashboardUrls.StructuredLogsUrl());

        AssertMenuItemIsActive(cut, Resources.Layout.NavMenuStructuredLogsTab);
    }

    [Fact]
    public void Render_OpenMenu_CurrentPageWithQueryStringHasSemanticAndVisualSelectedState()
    {
        var cut = RenderMobileNavMenu(DashboardUrls.StructuredLogsUrl(logLevel: "warning"));

        AssertMenuItemIsActive(cut, Resources.Layout.NavMenuStructuredLogsTab);
    }

    [Fact]
    public void Render_OpenMenu_ResourcesPageWithQueryStringHasSemanticAndVisualSelectedState()
    {
        var cut = RenderMobileNavMenu(DashboardUrls.ResourcesUrl(resource: "foo"));

        AssertMenuItemIsActive(cut, Resources.Layout.NavMenuResourcesTab);
    }

    [Fact]
    public void MobileNavMenu_ConstrainedToRemainingViewport()
    {
        var cut = RenderMobileNavMenu(DashboardUrls.ResourcesUrl());

        var style = cut.Find("fluent-menu").GetAttribute("style");

        Assert.Contains("max-height: calc(100dvh - var(--mobile-header-height) - var(--mobile-nav-menu-offset))", style);
        Assert.DoesNotContain("height: 100vh", style);
        Assert.Contains("margin-top: var(--mobile-nav-menu-offset)", style);
        Assert.Contains("overflow-y: auto", style);
    }

    private IRenderedComponent<MobileNavMenu> RenderMobileNavMenu(string currentUrl)
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        Services.AddSingleton<IDashboardClient>(new TestDashboardClient(isEnabled: true));
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        FluentUISetupHelpers.SetupFluentMenu(this);
        FluentUISetupHelpers.SetupFluentDivider(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);

        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo(currentUrl);

        return RenderComponent<MobileNavMenu>(builder =>
        {
            builder.Add(p => p.IsNavMenuOpen, true);
            builder.Add(p => p.IsAIEnabled, false);
            builder.Add(p => p.CloseNavMenu, () => { });
            builder.Add(p => p.LaunchHelpAsync, () => Task.CompletedTask);
            builder.Add(p => p.LaunchAIAgentsAsync, () => Task.CompletedTask);
            builder.Add(p => p.IsAgentHelpEnabled, false);
            builder.Add(p => p.LaunchAIAssistantAsync, () => Task.CompletedTask);
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
        // with mobile-nav-menu-icon-active so non-color cues stay alongside the
        // ::before accent bar styled in app.css.
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
