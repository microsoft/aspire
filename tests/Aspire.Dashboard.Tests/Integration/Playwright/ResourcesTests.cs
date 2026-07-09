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
    public async Task UrlLink_EnterDoesNotOpenResourceDetails()
    {
        await RunTestAsync(async page =>
        {
            await PlaywrightFixture.GoToHomeAndWaitForDataGridLoad(page).DefaultTimeout();

            var popup = await page.RunAndWaitForPopupAsync(async () =>
            {
                var urlLink = page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "about:blank#resource-url" }).First;
                await urlLink.FocusAsync();
                await page.Keyboard.PressAsync("Enter");
            });

            await popup.WaitForURLAsync("about:blank#resource-url").DefaultTimeout();
            await popup.CloseAsync();
            await Assertions.Expect(page.Locator(".details-header-title")).ToHaveCountAsync(0);
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ResourceTypeMenuItem_EnterShowsResourceTypes()
    {
        await RunTestAsync(async page =>
        {
            await PlaywrightFixture.GoToHomeAndWaitForDataGridLoad(page).DefaultTimeout();

            var viewOptionsButton = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = Dashboard.Resources.Resources.ResourcesChangeViewOptions, Exact = true });
            await viewOptionsButton.FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            var showResourceTypes = page.GetByRole(AriaRole.Menuitem, new PageGetByRoleOptions { Name = Dashboard.Resources.Resources.ResourcesShowTypes, Exact = true });
            await Assertions.Expect(showResourceTypes).ToBeVisibleAsync();

            await page.Keyboard.PressAsync("Tab");
            await page.Keyboard.PressAsync("ArrowDown");
            await Assertions.Expect(showResourceTypes).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Enter");

            await page.WaitForFunctionAsync("() => sessionStorage.getItem('Aspire_Resources_ShowResourceTypes') !== null").DefaultTimeout();
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

            var tableTab = page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = ControlsStrings.ResourcesContainerTableTab, Exact = true });
            await Assertions.Expect(tableTab).ToBeVisibleAsync();
            await Assertions.Expect(tableTab).ToHaveAttributeAsync("aria-selected", "true");

            var tabBounds = await tableTab.BoundingBoxAsync();
            Assert.NotNull(tabBounds);
            Assert.True(tabBounds.X >= 0);
            Assert.True(tabBounds.X + tabBounds.Width <= 320);
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ResourceViewTabs_RemainVisibleAtNarrowHorizontalViewport()
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(360, 720);
            await PlaywrightFixture.GoToHomeAndWaitForDataGridLoad(page).DefaultTimeout();

            var tabs = page.Locator(".resources-tab-header[orientation='horizontal']");
            await Assertions.Expect(tabs).ToBeVisibleAsync();

            var tableTab = page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = ControlsStrings.ResourcesContainerTableTab, Exact = true });
            var parametersTab = page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = ControlsStrings.ResourcesContainerParametersTab, Exact = true });
            var graphTab = page.Locator("#tab-Graph");

            await AssertTabVisibleWithinViewportAsync(tableTab, 360);
            await AssertTabVisibleWithinViewportAsync(parametersTab, 360);
            await AssertTabVisibleWithinViewportAsync(graphTab, 360);
        });
    }

    public sealed class ResourcesDashboardServerFixture : DashboardServerFixture
    {
        protected override IReadOnlyList<ResourceViewModel> Resources =>
        [
            ModelTestHelpers.CreateResource(
                resourceName: "TestResource",
                resourceType: KnownResourceTypes.Project,
                state: KnownResourceState.Running,
                urls:
                [
                    new UrlViewModel("http", new Uri("about:blank#resource-url"), isInternal: false, isInactive: false, UrlDisplayPropertiesViewModel.Empty)
                ]),
            ModelTestHelpers.CreateResource(
                resourceName: "HiddenResource",
                resourceType: KnownResourceTypes.Container,
                hidden: true)
        ];
    }

    private static async Task AssertTabVisibleWithinViewportAsync(ILocator tab, int viewportWidth)
    {
        await Assertions.Expect(tab).ToBeVisibleAsync();

        var tabBounds = await tab.BoundingBoxAsync();
        Assert.NotNull(tabBounds);
        Assert.True(tabBounds.X >= 0, $"Tab should be within the viewport, but its X position was {tabBounds.X}.");
        Assert.True(tabBounds.X + tabBounds.Width <= viewportWidth, $"Tab should fit inside the {viewportWidth}px viewport, but its right edge was {tabBounds.X + tabBounds.Width}.");
    }
}
