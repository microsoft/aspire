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
    public async Task ViewOptionsMenu_ReportsExpandedState()
    {
        await RunTestAsync(async page =>
        {
            await PlaywrightFixture.GoToHomeAndWaitForDataGridLoad(page).DefaultTimeout();

            var viewOptionsButton = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = Dashboard.Resources.Resources.ResourcesChangeViewOptions, Exact = true });
            var initialExpandedState = await viewOptionsButton.GetAttributeAsync("aria-expanded");
            Assert.Null(initialExpandedState);

            await viewOptionsButton.ClickAsync();
            await Assertions.Expect(viewOptionsButton).ToHaveAttributeAsync("aria-expanded", "true");

            var showResourceTypes = page.GetByRole(AriaRole.Menuitem, new PageGetByRoleOptions { Name = Dashboard.Resources.Resources.ResourcesShowTypes, Exact = true });
            await showResourceTypes.ClickAsync();
            await Assertions.Expect(viewOptionsButton).ToHaveAttributeAsync("aria-expanded", "false");
        });
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
    public async Task GridActionButtons_UseCompactMinimumWidth()
    {
        await RunTestAsync(async page =>
        {
            await PlaywrightFixture.GoToHomeAndWaitForDataGridLoad(page).DefaultTimeout();

            var values = await page.Locator(".grid-action-container fluent-button").EvaluateAllAsync<int[]>(
                """
                buttons => [
                    buttons.length,
                    buttons.filter(button => getComputedStyle(button).minWidth !== '32px').length
                ]
                """);

            Assert.True(values[0] > 0);
            Assert.Equal(0, values[1]);
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

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task GraphView_SwitchesWithoutReloadOrLayoutCollapse()
    {
        await RunTestAsync(async page =>
        {
            await PlaywrightFixture.GoToHomeAndWaitForDataGridLoad(page).DefaultTimeout();

            var navigationCount = await page.EvaluateAsync<int>("() => performance.getEntriesByType('navigation').length");

            var graphTab = page.Locator("#tab-Graph");
            await graphTab.ClickAsync();
            await Assertions.Expect(graphTab).ToHaveAttributeAsync("aria-selected", "true");

            var graphContainer = page.Locator("#resourcesGraphContainer");
            await Assertions.Expect(graphContainer).ToBeVisibleAsync();

            var graphWidth = await graphContainer.EvaluateAsync<double>("element => element.getBoundingClientRect().width");
            Assert.True(graphWidth > 100, $"The resource graph should fill the page content area, but its width was {graphWidth}px.");

            var navigationCountAfterSwitch = await page.EvaluateAsync<int>("() => performance.getEntriesByType('navigation').length");
            Assert.Equal(navigationCount, navigationCountAfterSwitch);
            await Assertions.Expect(page.Locator("#blazor-error-ui")).ToBeHiddenAsync();
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task GraphNode_RightClickOpensMenuAtCursorWithoutOverlay()
    {
        await RunTestAsync(async page =>
        {
            await page.GotoAsync("/?view=Graph");

            var node = page.Locator(".resource-node").First;
            await Assertions.Expect(node).ToBeVisibleAsync();
            var nodeBounds = await node.BoundingBoxAsync();
            Assert.NotNull(nodeBounds);

            var cursorX = nodeBounds.X + nodeBounds.Width / 2;
            var cursorY = nodeBounds.Y + nodeBounds.Height / 2;
            await node.Locator("xpath=..").EvaluateAsync(
                """
                element => {
                    const bounds = element.querySelector('.resource-node').getBoundingClientRect();
                    element.dispatchEvent(new MouseEvent('contextmenu', {
                        bubbles: true,
                        button: 2,
                        clientX: Math.round(bounds.left + bounds.width / 2),
                        clientY: Math.round(bounds.top + bounds.height / 2)
                    }));
                }
                """);

            var menu = page.Locator("fluent-menu.aspire-menu-container:not([trigger]) > fluent-menu-list");
            await Assertions.Expect(menu).ToBeVisibleAsync();
            var menuBounds = await menu.BoundingBoxAsync();
            Assert.NotNull(menuBounds);

            Assert.InRange(Math.Abs(menuBounds.X - cursorX), 0, 2);
            Assert.InRange(Math.Abs(menuBounds.Y - cursorY), 0, 2);

            var visibleBlockingIndicators = await page.Locator("fluent-overlay, fluent-progress-ring, fluent-spinner").EvaluateAllAsync<int>(
                "elements => elements.filter(element => element.getBoundingClientRect().width > 0 && element.getBoundingClientRect().height > 0).length");
            Assert.Equal(0, visibleBlockingIndicators);

            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(menu).ToBeHiddenAsync();

            await node.Locator("xpath=..").EvaluateAsync(
                """
                element => {
                    const bounds = element.querySelector('.resource-node').getBoundingClientRect();
                    element.dispatchEvent(new MouseEvent('contextmenu', {
                        bubbles: true,
                        button: 2,
                        clientX: Math.round(bounds.left + bounds.width / 2),
                        clientY: Math.round(bounds.top + bounds.height / 2)
                    }));
                }
                """);

            await Assertions.Expect(menu).ToBeVisibleAsync();
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
