// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
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

    [Fact]
    public async Task Render_OpenMenu_FeedbackEntryExpandsNestedItemsInlineWhenClicked()
    {
        var bugClicked = false;
        var closeCount = 0;
        var feedbackItems = new List<MenuButtonItem>
        {
            new() { Text = "Report a bug", OnClick = () => { bugClicked = true; return Task.CompletedTask; } },
            new() { Text = "Suggest an idea", OnClick = () => Task.CompletedTask },
            new() { Text = "General feedback", OnClick = () => Task.CompletedTask },
        };

        var cut = RenderMobileNavMenu(DashboardUrls.ResourcesUrl(), feedbackItems, closeNavMenu: () => closeCount++);

        // The feedback entry's nested items are collapsed until the entry is tapped. This is the fix
        // for the mobile bug where tapping feedback closed the menu and did nothing, because nested
        // submenus can't be opened via hover/expander affordances on touch.
        Assert.Empty(cut.FindAll("fluent-menu-item.mobile-nav-menu-nested-item"));

        var feedbackEntry = cut.FindAll("fluent-menu-item")
            .Single(item => item.GetAttribute("title") == Resources.Layout.MainLayoutProvideFeedback);
        Assert.Equal("menu", feedbackEntry.GetAttribute("aria-haspopup"));
        Assert.Equal("false", feedbackEntry.GetAttribute("aria-expanded"));

        // Tapping the feedback entry expands its nested items inline and keeps the nav menu open.
        await cut.InvokeAsync(() => feedbackEntry.Click());

        Assert.Equal(0, closeCount);
        Assert.Equal("true", cut.FindAll("fluent-menu-item")
            .Single(item => item.GetAttribute("title") == Resources.Layout.MainLayoutProvideFeedback)
            .GetAttribute("aria-expanded"));

        var nestedItemTexts = cut.FindAll("fluent-menu-item.mobile-nav-menu-nested-item")
            .Select(item => item.TextContent.Trim())
            .ToList();
        Assert.Equal(new[] { "Report a bug", "Suggest an idea", "General feedback" }, nestedItemTexts);

        // Tapping a nested item closes the nav menu and invokes that item's action.
        await cut.InvokeAsync(() => cut.FindAll("fluent-menu-item.mobile-nav-menu-nested-item")
            .Single(item => item.TextContent.Contains("Report a bug", StringComparison.Ordinal))
            .Click());

        Assert.True(bugClicked);
        Assert.Equal(1, closeCount);
    }

    private IRenderedComponent<MobileNavMenu> RenderMobileNavMenu(string currentUrl)
    {
        return RenderMobileNavMenu(currentUrl, feedbackMenuItems: []);
    }

    private IRenderedComponent<MobileNavMenu> RenderMobileNavMenu(string currentUrl, List<MenuButtonItem> feedbackMenuItems, Action? closeNavMenu = null)
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
            builder.Add(p => p.CloseNavMenu, closeNavMenu ?? (() => { }));
            builder.Add(p => p.GetFeedbackMenuItems, () => feedbackMenuItems);
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
