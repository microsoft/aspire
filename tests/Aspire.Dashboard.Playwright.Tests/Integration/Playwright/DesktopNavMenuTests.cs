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

            await page.WaitForURLAsync(url => new Uri(url).AbsolutePath.StartsWith("/resources", StringComparison.Ordinal));
            await Assertions.Expect(page.Locator(".resource-card").First).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Tablist, new() { Name = Resources.Layout.ResourceTabsLandmark })).ToBeVisibleAsync();
            await AssertActiveItemAsync(Resources.Layout.ResourceTabOverview);

            var navigationCount = await page.EvaluateAsync<int>("() => performance.getEntriesByType('navigation').length");

            foreach (var (label, path) in new[]
            {
                (Resources.Layout.NavMenuConsoleLogsTab, "/consolelogs"),
                (Resources.StructuredLogs.StructuredLogsHeader, "/structuredlogs"),
                (Resources.Layout.NavMenuTracesTab, "/traces"),
                (Resources.Layout.ResourceTabOverview, "/resources")
            })
            {
                var tab = page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = label, Exact = true });
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
                var activeItem = page.Locator("fluent-tab[aria-selected='true']");
                await Assertions.Expect(activeItem).ToHaveCountAsync(1);
                await Assertions.Expect(activeItem).ToHaveAccessibleNameAsync(expectedName);
            }
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ResourcePaneToggle_ChangesLayoutAndPersistsCollapsedState()
    {
        await RunTestAsync(async page =>
        {
            await page.GotoAsync("/resources");
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
