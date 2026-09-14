// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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
    public async Task Toggle_ChangesLayoutAndPersistsExpandedState()
    {
        await RunTestAsync(async page =>
        {
            await page.GotoAsync("/");
            await page.EvaluateAsync($"localStorage.setItem('{BrowserStorageKeys.NavMenuExpanded}', 'false')");
            await page.ReloadAsync();

            await AssertNavigationLayoutAsync(expanded: false);

            await page.Locator(".nav-toggle-button").EvaluateAsync("element => element.click()");
            await AssertNavigationLayoutAsync(expanded: true);

            await page.ReloadAsync();
            await AssertNavigationLayoutAsync(expanded: true);

            async Task AssertNavigationLayoutAsync(bool expanded)
            {
                var layout = page.Locator(".layout");
                await Assertions.Expect(layout).ToHaveClassAsync(expanded ? new System.Text.RegularExpressions.Regex("nav-expanded") : new System.Text.RegularExpressions.Regex("nav-collapsed"));

                var values = await layout.EvaluateAsync<double[]>(
                    """
                    (layout, expanded) => {
                        const rail = layout.querySelector('.desktop-nav-rail');
                        const item = rail.querySelector('.fluent-appbar-item');
                        const stack = item.querySelector('.fluent-appbaritem-stack');
                        const label = item.querySelector('[part="label"]');

                        return [
                            rail.getBoundingClientRect().width,
                            item.getBoundingClientRect().height,
                            getComputedStyle(stack).flexDirection === (expanded ? 'row' : 'column') ? 1 : 0,
                            label.getBoundingClientRect().width,
                            label.getBoundingClientRect().height
                        ];
                    }
                    """,
                    expanded);

                Assert.InRange(values[0], expanded ? 170 : 64, expanded ? 190 : 72);
                Assert.InRange(values[1], 44, 52);
                Assert.Equal(1, values[2]);
                Assert.True(expanded ? values[3] > 20 : values[3] <= 1);
                Assert.True(expanded ? values[4] > 10 : values[4] <= 1);
            }
        });
    }
}