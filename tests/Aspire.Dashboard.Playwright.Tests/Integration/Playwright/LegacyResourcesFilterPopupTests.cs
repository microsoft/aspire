// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;
using Aspire.TestUtilities;
using Aspire.Tests.Shared.DashboardModel;
using Microsoft.Playwright;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration.Playwright;

[RequiresFeature(TestFeature.Playwright)]
public class LegacyResourcesFilterPopupTests : PlaywrightTestsBase<LegacyResourcesFilterPopupTests.ResourceGraphDisabledDashboardServerFixture>
{
    public LegacyResourcesFilterPopupTests(ResourceGraphDisabledDashboardServerFixture dashboardServerFixture)
        : base(dashboardServerFixture)
    {
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task FilterPopup_ResourceGraphDisabled_StaysInsideViewport()
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(1280, 900);
            var baseUrl = DashboardServerFixture.DashboardApp.FrontendSingleEndPointAccessor().GetResolvedAddress();
            await page.Context.AddCookiesAsync([new Cookie { Name = "Aspire_Dashboard_Experience", Value = "legacy", Url = baseUrl }]);
            await page.GotoAsync("/legacy/resources");

            // Without the graph there is no tabs row, so the filter button renders in the page header toolbar.
            await Assertions.Expect(page.Locator(".resources-tab-header")).ToHaveCountAsync(0);
            var filterButton = page.Locator("#resourceFilterButton");
            await filterButton.ClickAsync();

            var dialog = page.Locator(".resources-filter-popup [part~='dialog']");
            await Assertions.Expect(dialog).ToBeVisibleAsync();

            var buttonBox = await filterButton.BoundingBoxAsync();
            var dialogBox = await dialog.BoundingBoxAsync();
            Assert.NotNull(buttonBox);
            Assert.NotNull(dialogBox);

            Assert.True(dialogBox.X >= 0, $"Filter popup starts at x={dialogBox.X}, outside the left edge of the viewport.");
            Assert.True(dialogBox.X + dialogBox.Width <= 1280, $"Filter popup ends at x={dialogBox.X + dialogBox.Width}, outside the right edge of the viewport.");
            Assert.True(dialogBox.Y >= buttonBox.Y + buttonBox.Height - 1, $"Filter popup at y={dialogBox.Y} should open below the filter button (bottom={buttonBox.Y + buttonBox.Height}).");
        });
    }

    public sealed class ResourceGraphDisabledDashboardServerFixture : DashboardServerFixture
    {
        public ResourceGraphDisabledDashboardServerFixture()
        {
            Configuration["Dashboard:UI:DisableResourceGraph"] = "true";
        }

        protected override IReadOnlyList<ResourceViewModel> Resources =>
        [
            ModelTestHelpers.CreateResource(
                resourceName: "apigateway",
                resourceType: KnownResourceTypes.Project,
                state: KnownResourceState.Running),
            ModelTestHelpers.CreateResource(
                resourceName: "basketcache",
                resourceType: KnownResourceTypes.Container,
                state: KnownResourceState.Running)
        ];
    }
}
