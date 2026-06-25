// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;
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
            await page.Keyboard.PressAsync("Tab");

            await Assertions.Expect(page.GetByRole(AriaRole.Menu)).ToBeHiddenAsync();
            Assert.Equal(Dashboard.Resources.Layout.NavMenuResourcesTab, await GetActiveElementNameAsync(page));
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ResourceFilterPopup_BoundaryTabKeysAndEscapeClosePopup()
    {
        await RunTestAsync(async page =>
        {
            await PlaywrightFixture.GoToHomeAndWaitForDataGridLoad(page).DefaultTimeout();

            var resourceFilter = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = Dashboard.Resources.Resources.ResourcesNotFiltered });
            var popup = page.Locator(".resources-filter-popup");
            var popupCheckboxes = popup.Locator("fluent-checkbox");

            await OpenResourceFilterAsync(resourceFilter, popup);
            await popupCheckboxes.Last.FocusAsync();
            await page.Keyboard.PressAsync("Tab");

            await Assertions.Expect(popup).ToBeHiddenAsync();
            Assert.Equal(Dashboard.Resources.Resources.ResourcesNotFiltered, await GetActiveElementNameAsync(page));

            await OpenResourceFilterAsync(resourceFilter, popup);
            await popupCheckboxes.First.FocusAsync();
            await page.Keyboard.PressAsync("Shift+Tab");

            await Assertions.Expect(popup).ToBeHiddenAsync();
            Assert.Equal(Dashboard.Resources.Resources.ResourcesNotFiltered, await GetActiveElementNameAsync(page));

            await OpenResourceFilterAsync(resourceFilter, popup);
            await popupCheckboxes.First.FocusAsync();
            await page.Keyboard.PressAsync("Escape");

            await Assertions.Expect(popup).ToBeHiddenAsync();
            Assert.Equal(Dashboard.Resources.Resources.ResourcesNotFiltered, await GetActiveElementNameAsync(page));
        });
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

    private static async Task OpenResourceFilterAsync(ILocator resourceFilter, ILocator popup)
    {
        await resourceFilter.ClickAsync();
        await Assertions.Expect(popup).ToBeVisibleAsync();
    }

    private static async Task<string?> GetActiveElementNameAsync(IPage page)
    {
        return await page.EvaluateAsync<string?>(
            """
            () => {
                const activeElement = document.activeElement;
                return activeElement?.getAttribute('aria-label')
                    ?? activeElement?.getAttribute('title')
                    ?? activeElement?.textContent?.trim().replace(/\s+/g, ' ');
            }
            """);
    }
}
