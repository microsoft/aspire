// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;
using Aspire.TestUtilities;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Playwright;
using Xunit;
using DashboardResources = Aspire.Dashboard.Resources.Resources;

namespace Aspire.Dashboard.Tests.Integration.Playwright;

[RequiresFeature(TestFeature.Playwright)]
public class ResourcesTests : PlaywrightTestsBase<DashboardServerFixture>
{
    public ResourcesTests(DashboardServerFixture dashboardServerFixture)
        : base(dashboardServerFixture)
    {
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ResourceToolbarTooltip_ShowsOnKeyboardFocusAndHidesOnEscape()
    {
        await RunTestAsync(async page =>
        {
            await PlaywrightFixture.GoToHomeAndWaitForDataGridLoad(page).DefaultTimeout();

            var filterButton = page.Locator("#resourceFilterButton");
            await filterButton.FocusAsync().DefaultTimeout(TestConstants.LongTimeoutTimeSpan);

            var tooltip = page.Locator("fluent-tooltip[anchor='resourceFilterButton']");
            await Assertions
                .Expect(tooltip)
                .ToHaveAttributeAsync("visible", string.Empty)
                .DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
            await Assertions
                .Expect(tooltip)
                .ToHaveTextAsync(DashboardResources.ResourcesNotFiltered)
                .DefaultTimeout(TestConstants.LongTimeoutTimeSpan);

            await page.Keyboard.PressAsync("Escape").DefaultTimeout(TestConstants.LongTimeoutTimeSpan);

            await Assertions
                .Expect(tooltip)
                .Not
                .ToHaveAttributeAsync("visible", string.Empty)
                .DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
        });
    }
}
