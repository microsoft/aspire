// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Resources;
using Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;
using Aspire.Dashboard.Utils;
using Aspire.TestUtilities;
using Microsoft.Playwright;
using Xunit;
using ResourceStrings = Aspire.Dashboard.Resources.Resources;

namespace Aspire.Dashboard.Tests.Integration.Playwright;

[RequiresFeature(TestFeature.Playwright)]
public class OverviewLayoutTests(ResourcesTests.ResourcesDashboardServerFixture dashboardServerFixture)
    : PlaywrightTestsBase<ResourcesTests.ResourcesDashboardServerFixture>(dashboardServerFixture)
{
    [Theory]
    [InlineData(390, 844)]
    [InlineData(768, 1024)]
    [InlineData(1440, 900)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task DefaultLayout_AffectsNewTabsButNotNavigationOrReload(int width, int height)
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(width, height);
            await page.GotoAsync("/resources");
            await Assertions.Expect(page.Locator(".home-hero")).ToBeVisibleAsync();

            await ChangeDefaultAsync(page, ResourceStrings.GraphPageTitle);
            await Assertions.Expect(page.Locator(".home-hero")).ToBeVisibleAsync();
            var futureGraph = await page.Context.NewPageAsync();
            await futureGraph.SetViewportSizeAsync(width, height);
            await futureGraph.GotoAsync("/resources");
            await Assertions.Expect(futureGraph.Locator(".home-resource-graph-layout")).ToBeVisibleAsync();
            await futureGraph.CloseAsync();

            await ChangeCurrentAsync(page, ResourceStrings.GraphPageTitle);
            await ChangeDefaultAsync(page, Dialogs.SettingsDialogGroupOverviewSummary);
            await Assertions.Expect(page.Locator(".home-resource-graph-layout")).ToBeVisibleAsync();

            var documentStartedAt = await page.EvaluateAsync<double>("performance.timeOrigin");
            await NavigateAsync(page, Layout.NavMenuTracesTab, "/traces");
            await Assertions.Expect(page.GetByRole(AriaRole.Region, new() { Name = Traces.TracesHeader, Exact = true })).ToBeVisibleAsync();
            await NavigateAsync(page, Layout.ResourceTabOverview, "/resources");
            await Assertions.Expect(page.Locator(".home-resource-graph-layout")).ToBeVisibleAsync();
            Assert.Equal(documentStartedAt, await page.EvaluateAsync<double>("performance.timeOrigin"));
            await page.ReloadAsync();
            await Assertions.Expect(page.Locator(".home-resource-graph-layout")).ToBeVisibleAsync();
            Assert.Equal("reload", await page.EvaluateAsync<string>("performance.getEntriesByType('navigation')[0].type"));

            var ordinaryTab = await page.Context.NewPageAsync();
            await ordinaryTab.SetViewportSizeAsync(width, height);
            await ordinaryTab.GotoAsync("/resources");
            await Assertions.Expect(ordinaryTab.Locator(".home-hero")).ToBeVisibleAsync();
            await ordinaryTab.CloseAsync();

            // window.open copies sessionStorage into the new tab; it must not copy the temporary layout.
            var openerTab = await page.RunAndWaitForPopupAsync(() => page.EvaluateAsync("window.open('/resources', '_blank')"));
            await openerTab.SetViewportSizeAsync(width, height);
            await Assertions.Expect(openerTab.Locator(".home-hero")).ToBeVisibleAsync();
            Assert.True(await openerTab.EvaluateAsync<bool>("window.opener !== null"));
            await openerTab.CloseAsync();
            await Assertions.Expect(page.Locator(".home-resource-graph-layout")).ToBeVisibleAsync();

            await page.CloseAsync();
            var reopenedTab = await page.Context.NewPageAsync();
            await reopenedTab.SetViewportSizeAsync(width, height);
            await reopenedTab.GotoAsync("/resources");
            await Assertions.Expect(reopenedTab.Locator(".home-hero")).ToBeVisibleAsync();
            await reopenedTab.CloseAsync();
        });
    }

    private static async Task ChangeCurrentAsync(IPage page, string layout)
    {
        var picker = page.Locator(".overview-layout-menu");
        if (await picker.IsVisibleAsync())
        {
            await picker.ClickAsync();
            await page.GetByRole(AriaRole.Menuitemradio, new() { Name = layout, Exact = true }).ClickAsync();
        }
        else
        {
            await page.GetByRole(AriaRole.Group, new() { Name = ResourceStrings.LayoutLabel, Exact = true })
                .GetByRole(AriaRole.Button, new() { Name = layout, Exact = true }).ClickAsync();
        }
    }

    private static async Task ChangeDefaultAsync(IPage page, string layout)
    {
        await Assertions.Expect(page.Locator(".overview-header-actions")).ToBeVisibleAsync();
        var settingsButton = page.GetByRole(AriaRole.Button, new() { Name = Layout.MainLayoutLaunchSettings, Exact = true });
        if (await settingsButton.IsVisibleAsync())
        {
            await settingsButton.ClickAsync();
        }
        else
        {
            await page.GetByRole(AriaRole.Button, new() { Name = "General", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Menuitem, new() { Name = Layout.MainLayoutLaunchSettings, Exact = true }).ClickAsync();
        }
        await page.GetByRole(AriaRole.Group, new() { Name = Dialogs.SettingsDialogGroupOverview, Exact = true })
            .GetByRole(AriaRole.Button, new() { Name = layout, Exact = true }).ClickAsync();
        await page.WaitForFunctionAsync(
            "expected => JSON.parse(localStorage.getItem(expected.key)) === expected.value",
            new { key = BrowserStorageKeys.GroupOverview, value = layout == ResourceStrings.GraphPageTitle ? "Graph" : "Summary" });
        await page.GetByRole(AriaRole.Button, new() { Name = Dialogs.DialogCloseButtonText, Exact = true }).First.ClickAsync();
    }

    private static async Task NavigateAsync(IPage page, string section, string path)
    {
        var compactTabs = page.Locator(".resource-tabs-compact-button:visible");
        var tab = page.GetByRole(AriaRole.Tab, new() { Name = section, Exact = true });
        if (await compactTabs.IsVisibleAsync())
        {
            await compactTabs.ClickAsync();
            await page.GetByRole(AriaRole.Menuitemradio, new() { Name = section, Exact = true }).ClickAsync();
        }
        else if (await tab.IsVisibleAsync())
        {
            var bounds = await tab.BoundingBoxAsync();
            Assert.NotNull(bounds);
            await page.Mouse.ClickAsync(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        }
        else
        {
            await page.Locator(".resource-tabs-overflow-button:visible").ClickAsync();
            await page.GetByRole(AriaRole.Menuitemradio, new() { Name = section, Exact = true }).ClickAsync();
        }
        await page.WaitForFunctionAsync("path => window.location.pathname === path", path);
    }
}
