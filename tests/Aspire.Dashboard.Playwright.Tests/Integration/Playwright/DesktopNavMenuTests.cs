// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;
using Aspire.Dashboard.Utils;
using Aspire.TestUtilities;
using Microsoft.Playwright;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration.Playwright;

[RequiresFeature(TestFeature.Playwright)]
public sealed class DesktopNavMenuTests : PlaywrightTestsBase<DashboardServerFixture>
{
    public DesktopNavMenuTests(DashboardServerFixture dashboardServerFixture)
        : base(dashboardServerFixture)
    {
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task MainNavigation_NavigatesBetweenSectionsAndMarksActiveItem()
    {
        await RunTestAsync(async page =>
        {
            await page.GotoAsync("/");

            // The dashboard opens on its home page: the list of all resources, filling the page, with no view selected.
            await Assertions.Expect(page.Locator(".resources-layout.pane-full-canvas")).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Navigation, new() { Name = Resources.Layout.ResourceTabsLandmark })).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("a.resource-tab[aria-current='page']:visible")).ToHaveCountAsync(0);

            var navigationCount = await page.EvaluateAsync<int>("() => performance.getEntriesByType('navigation').length");

            foreach (var (label, path) in new[]
            {
                (Resources.Layout.NavMenuConsoleLogsTab, "/consolelogs"),
                (Resources.StructuredLogs.StructuredLogsHeader, "/structuredlogs"),
                (Resources.Layout.NavMenuTracesTab, "/traces"),
                (Resources.Layout.ResourceTabOverview, "/resources")
            })
            {
                var tab = page.Locator(".resources-tab-header").GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = label, Exact = true });
                var tabBounds = await tab.BoundingBoxAsync();
                Assert.NotNull(tabBounds);
                await page.Mouse.ClickAsync(tabBounds.X + tabBounds.Width / 2, tabBounds.Y + tabBounds.Height / 2);
                await page.WaitForFunctionAsync(
                    "path => window.location.pathname.startsWith(path)",
                    path);
                await AssertActiveItemAsync(label);
            }

            Assert.Equal(navigationCount, await page.EvaluateAsync<int>("() => performance.getEntriesByType('navigation').length"));

            async Task AssertActiveItemAsync(string expectedName)
            {
                var activeItem = page.Locator("a.resource-tab[aria-current='page']:visible");
                await Assertions.Expect(activeItem).ToHaveCountAsync(1);
                await Assertions.Expect(activeItem).ToHaveAccessibleNameAsync(expectedName);
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task MainNavigation_MiddleOrModifiedClickOpensTabInNewPage(bool middleClick)
    {
        await RunTestAsync(async page =>
        {
            await page.GotoAsync("/");
            var tab = page.Locator(".resources-tab-header").GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = Resources.Layout.NavMenuTracesTab, Exact = true });
            await Assertions.Expect(tab).ToHaveAttributeAsync("href", new Regex("/traces$"));

            var newPage = await page.Context.RunAndWaitForPageAsync(() => middleClick
                ? tab.ClickAsync(new LocatorClickOptions { Button = MouseButton.Middle })
                : tab.ClickAsync(new LocatorClickOptions { Modifiers = [KeyboardModifier.ControlOrMeta] }));
            try
            {
                await Assertions.Expect(newPage).ToHaveURLAsync(new Regex("/traces$"));
                await Assertions.Expect(newPage.Locator(".resources-tab-header a.resource-tab[aria-current='page']")).ToHaveAccessibleNameAsync(Resources.Layout.NavMenuTracesTab);

                // The browser opens the link; the dashboard doesn't navigate the original page too.
                await page.WaitForTimeoutAsync(500);
                await Assertions.Expect(page).ToHaveURLAsync(ResourcePaneNavigation.HomeUrl);
                await Assertions.Expect(page.Locator(".resources-layout.pane-full-canvas")).ToBeVisibleAsync();
            }
            finally
            {
                await newPage.CloseAsync();
            }
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ResourcePaneToggle_ChangesLayoutAndPersistsCollapsedState()
    {
        await RunTestAsync(async page =>
        {
            // A page with content: the dashboard's start page, /resources, opens on the full-page list instead.
            await page.GotoAsync("/consolelogs");
            await page.EvaluateAsync($"localStorage.setItem('{BrowserStorageKeys.ResourcePaneCollapsed}', 'false')");
            await page.ReloadAsync();

            var layout = page.Locator(".resources-layout");
            await Assertions.Expect(layout).Not.ToHaveClassAsync(new Regex("pane-collapsed"));

            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = Resources.Layout.ResourcePaneCollapse, Exact = true }).ClickAsync();
            await Assertions.Expect(layout).ToHaveClassAsync(new Regex("pane-collapsed"));

            await page.ReloadAsync();
            await Assertions.Expect(layout).ToHaveClassAsync(new Regex("pane-collapsed"));

            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = Resources.Layout.ResourcePaneExpand, Exact = true }).ClickAsync();
            await Assertions.Expect(layout).Not.ToHaveClassAsync(new Regex("pane-collapsed"));
        });
    }
}
