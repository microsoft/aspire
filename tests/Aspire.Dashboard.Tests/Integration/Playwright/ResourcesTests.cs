// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;
using Aspire.Dashboard.Resources;
using Aspire.TestUtilities;
using Aspire.Tests.Shared.DashboardModel;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Playwright;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration.Playwright;

[RequiresFeature(TestFeature.Playwright)]
public class ResourcesTests : PlaywrightTestsBase<ResourcesTests.ResourcesDashboardServerFixture>
{
    public ResourcesTests(ResourcesDashboardServerFixture dashboardServerFixture)
        : base(dashboardServerFixture)
    {
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ViewOptionsMenu_TabMovesFocusToNextLogicalControl()
    {
        await RunTestAsync(async page =>
        {
            await PlaywrightFixture.GoToHomeAndWaitForDataGridLoad(page).DefaultTimeout();

            var viewOptions = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = Dashboard.Resources.Resources.ResourcesChangeViewOptions });
            await viewOptions.ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Menu)).ToBeVisibleAsync();

            await page.Keyboard.PressAsync("Tab");
            await Assertions.Expect(page.GetByRole(AriaRole.Menu)).ToBeVisibleAsync();
            Assert.Equal(Dashboard.Resources.Resources.ResourceCollapseAllChildren, await GetActiveElementNameAsync(page));

            await page.Keyboard.PressAsync("Tab");

            await Assertions.Expect(page.GetByRole(AriaRole.Menu)).ToBeHiddenAsync();
            Assert.Equal(Layout.NavMenuResourcesTab, await GetActiveElementNameAsync(page));
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ResourceViewTabs_RemainVisibleAtNarrowViewport()
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(320, 720);
            await PlaywrightFixture.GoToHomeAndWaitForDataGridLoad(page).DefaultTimeout();

            var tableTab = page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = ControlsStrings.ChartContainerTableTab, Exact = true });
            await Assertions.Expect(tableTab).ToBeVisibleAsync();
            await Assertions.Expect(tableTab).ToHaveAttributeAsync("aria-selected", "true");

            var tabBounds = await tableTab.BoundingBoxAsync();
            Assert.NotNull(tabBounds);
            Assert.True(tabBounds.X >= 0);
            Assert.True(tabBounds.X + tabBounds.Width <= 320);
        });
    }

    private static Task<string?> GetActiveElementNameAsync(IPage page)
    {
        return page.EvaluateAsync<string?>(
            """
            () => {
                const activeElement = document.activeElement;
                return activeElement?.getAttribute('aria-label')
                    ?? activeElement?.getAttribute('title')
                    ?? activeElement?.textContent?.trim().replace(/\s+/g, ' ');
            }
            """);
    }

    public sealed class ResourcesDashboardServerFixture : DashboardServerFixture
    {
        protected override IReadOnlyList<ResourceViewModel> Resources =>
        [
            MockDashboardClient.TestResource1,
            ModelTestHelpers.CreateResource(
                resourceName: "HiddenResource",
                resourceType: KnownResourceTypes.Container,
                hidden: true)
        ];
    }
}
