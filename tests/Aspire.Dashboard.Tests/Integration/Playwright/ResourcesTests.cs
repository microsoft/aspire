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
    public async Task NameColumn_SortsResources()
    {
        await RunTestAsync(async page =>
        {
            await page.GotoAsync("/");

            var resourceNames = page.Locator(".main-grid .resource-row .resource-name-text");
            await Assertions.Expect(resourceNames).ToHaveCountAsync(3);
            await Assertions.Expect(resourceNames.Nth(0)).ToContainTextAsync("basketcache");
            await Assertions.Expect(resourceNames.Nth(1)).ToContainTextAsync("apigateway");
            await Assertions.Expect(resourceNames.Nth(2)).ToContainTextAsync("TestResource");

            var nameHeader = page.Locator(".main-grid th[col-index='1']");
            await nameHeader.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = ControlsStrings.NameColumnHeader, Exact = true }).ClickAsync();

            var sortItem = page.GetByRole(AriaRole.Menuitem, new PageGetByRoleOptions { Name = ControlsStrings.FluentDataGridHeaderCellSortButtonText, Exact = true });
            await sortItem.ClickAsync();

            await Assertions.Expect(nameHeader).ToHaveAttributeAsync("aria-sort", "ascending");
            await Assertions.Expect(resourceNames.Nth(0)).ToContainTextAsync("apigateway");
            await Assertions.Expect(resourceNames.Nth(1)).ToContainTextAsync("basketcache");
            await Assertions.Expect(resourceNames.Nth(2)).ToContainTextAsync("TestResource");
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
    public async Task ResourceGraphCog_IsKeyboardAccessibleAndDoesNotDragNode()
    {
        await RunTestAsync(async page =>
        {
            await PlaywrightFixture.GoToHomeAndWaitForDataGridLoad(page).DefaultTimeout();
            await page.Locator("#tab-Graph").ClickAsync();

            var node = page.Locator(".resource-group[resource-name='TestResource']");
            await Assertions.Expect(node).ToBeVisibleAsync();
            await node.HoverAsync();

            var resourceActionsLabel = string.Format(
                Dashboard.Resources.Resources.ResourcesGraphResourceActionsButton,
                "TestResource");
            var otherResourceActionsLabel = string.Format(
                Dashboard.Resources.Resources.ResourcesGraphResourceActionsButton,
                "apigateway");
            var cog = node.GetByRole(
                AriaRole.Button,
                new LocatorGetByRoleOptions
                {
                    Name = resourceActionsLabel,
                    Exact = true
                });
            await Assertions.Expect(cog).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByRole(
                AriaRole.Button,
                new PageGetByRoleOptions
                {
                    Name = otherResourceActionsLabel,
                    Exact = true
                })).ToHaveCountAsync(1);

            // Attempt a large drag from the cog. D3's drag behavior is attached to the ancestor
            // resource group, so this verifies the cog stops the initiating pointer event.
            await page.WaitForTimeoutAsync(300);
            var nodeBoundsBefore = await node.BoundingBoxAsync();
            var cogBounds = await cog.BoundingBoxAsync();
            Assert.NotNull(nodeBoundsBefore);
            Assert.NotNull(cogBounds);

            await page.Mouse.MoveAsync(
                cogBounds.X + cogBounds.Width / 2,
                cogBounds.Y + cogBounds.Height / 2);
            await page.Mouse.DownAsync();
            Assert.Equal(
                "none",
                await cog.EvaluateAsync<string>("element => getComputedStyle(element).outlineStyle"));
            await page.Mouse.MoveAsync(
                cogBounds.X + cogBounds.Width / 2 + 80,
                cogBounds.Y + cogBounds.Height / 2 + 80,
                new MouseMoveOptions { Steps = 5 });
            await page.Mouse.UpAsync();

            var nodeBoundsAfter = await node.BoundingBoxAsync();
            Assert.NotNull(nodeBoundsAfter);
            Assert.InRange(Math.Abs(nodeBoundsAfter.X - nodeBoundsBefore.X), 0, 5);
            Assert.InRange(Math.Abs(nodeBoundsAfter.Y - nodeBoundsBefore.Y), 0, 5);
            Assert.False((await node.GetAttributeAsync("class"))?.Split(' ').Contains("resource-group-selected"));

            var menu = page.GetByRole(
                AriaRole.Menu,
                new PageGetByRoleOptions { Name = "TestResource", Exact = true });
            await Assertions.Expect(menu).ToBeHiddenAsync();

            await node.HoverAsync();
            await cog.ClickAsync();
            await Assertions.Expect(menu).ToBeVisibleAsync();
            await Assertions.Expect(cog).ToHaveAttributeAsync("aria-expanded", "true");
            Assert.False((await node.GetAttributeAsync("class"))?.Split(' ').Contains("resource-group-selected"));

            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(menu).ToBeHiddenAsync();
            await Assertions.Expect(cog).ToHaveAttributeAsync("aria-expanded", "false");

            await node.HoverAsync();
            await cog.FocusAsync();
            await page.Keyboard.PressAsync("Enter");

            await Assertions.Expect(menu).ToBeVisibleAsync();
            await Assertions.Expect(cog).ToHaveAttributeAsync("aria-haspopup", "menu");
            await Assertions.Expect(cog).ToHaveAttributeAsync("aria-expanded", "true");
            var header = menu.Locator(".aspire-menu-header");
            await Assertions.Expect(header.Locator(".aspire-menu-header-text")).ToHaveTextAsync("TestResource");
            var headerBounds = await header.BoundingBoxAsync();
            Assert.NotNull(headerBounds);
            Assert.InRange(headerBounds.Height, 39, 41);

            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(menu).ToBeHiddenAsync();
            await Assertions.Expect(cog).ToHaveAttributeAsync("aria-expanded", "false");
            await Assertions.Expect(cog).ToBeFocusedAsync();

            await page.Keyboard.PressAsync("Enter");
            await page.GetByRole(
                AriaRole.Menuitem,
                new PageGetByRoleOptions
                {
                    Name = ControlsStrings.ActionViewDetailsText,
                    Exact = true
                }).ClickAsync();
            await Assertions.Expect(page.Locator(".details-header-title")).ToHaveTextAsync("TestResource");

            await page.GetByRole(
                AriaRole.Button,
                new PageGetByRoleOptions
                {
                    Name = ControlsStrings.SummaryDetailsViewCloseView,
                    Exact = true
                }).ClickAsync();
            await Assertions.Expect(page.Locator(".details-header-title")).ToHaveCountAsync(0);
            await Assertions.Expect(cog).ToBeFocusedAsync();
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
                resourceName: "apigateway",
                resourceType: KnownResourceTypes.Project,
                state: KnownResourceState.Running),
            ModelTestHelpers.CreateResource(
                resourceName: "basketcache",
                resourceType: KnownResourceTypes.Container,
                state: KnownResourceState.Running),
            ModelTestHelpers.CreateResource(
                resourceName: "TestResource",
                resourceType: KnownResourceTypes.Project,
                state: KnownResourceState.Running,
                urls:
                [
                    new UrlViewModel("http", new Uri("about:blank#resource-url"), isInternal: false, isInactive: false, UrlDisplayPropertiesViewModel.Empty)
                ])
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
