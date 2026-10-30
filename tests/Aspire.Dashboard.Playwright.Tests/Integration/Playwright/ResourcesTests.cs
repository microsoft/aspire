// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
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
    public async Task ResourcePane_GroupOrderSupportsDragKeyboardAndReload()
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(1440, 1000);
            await page.GotoAsync("/resources");
            await page.Locator("#resourcePaneGroupingButton").ClickAsync();
            await page.GetByRole(AriaRole.Menuitemradio, new PageGetByRoleOptions { Name = Dashboard.Resources.Layout.ResourcePaneGroupingResourceType, Exact = true }).ClickAsync();
            var pane = page.Locator(".resource-pane");
            var groups = pane.Locator(".resource-tag-group");
            await Assertions.Expect(groups).ToHaveCountAsync(2);
            var originalNames = await groups.EvaluateAllAsync<string[]>("groups => groups.map(group => group.getAttribute('aria-label'))");
            var sourceKey = await groups.Last.GetAttributeAsync("data-resource-group-key");
            await groups.Last.Locator("[data-resource-group-drag-handle]").DragToAsync(groups.First,
                new() { TargetPosition = new() { X = 8, Y = 2 } });
            await Assertions.Expect(groups.Locator(".resource-tag-name")).ToHaveTextAsync(originalNames.Reverse());
            await page.ReloadAsync();
            await Assertions.Expect(groups.Locator(".resource-tag-name")).ToHaveTextAsync(originalNames.Reverse());

            var sourceHandle = pane.Locator($"[data-resource-group-key='{sourceKey}'] [data-resource-group-drag-handle]");
            await sourceHandle.FocusAsync();
            await sourceHandle.PressAsync("Alt+ArrowDown");
            await Assertions.Expect(groups.Locator(".resource-tag-name")).ToHaveTextAsync(originalNames);
            await Assertions.Expect(sourceHandle).ToBeFocusedAsync();

            var search = pane.GetByRole(AriaRole.Searchbox);
            await search.FillAsync("api");
            await Assertions.Expect(pane.Locator("[data-resource-group-drag-handle]")).ToHaveCountAsync(0);
            await search.FillAsync(string.Empty);
            await Assertions.Expect(groups.Locator(".resource-tag-name")).ToHaveTextAsync(originalNames);
            await Assertions.Expect(pane.Locator("[data-resource-group-drag-handle]")).ToHaveCountAsync(2);
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ResourcePane_ReordersByDraggingAndKeyboardAndRemembersOrder()
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(1280, 900);
            await page.GotoAsync("/resources");

            var pane = page.Locator(".resource-pane");
            var rows = pane.Locator("[data-resource-name]:has([data-resource-drag-handle])");
            await Assertions.Expect(rows).ToHaveCountAsync(3);
            var originalOrder = await rows.EvaluateAllAsync<string[]>("rows => rows.map(row => row.dataset.resourceName)");
            var source = rows.Last.Locator("[data-resource-drag-handle]");
            await source.DragToAsync(rows.First, new LocatorDragToOptions { TargetPosition = new TargetPosition { X = 8, Y = 2 } });
            var reordered = new[] { originalOrder[2], originalOrder[0], originalOrder[1] };
            await Assertions.Expect(rows.Locator(".resource-row-name")).ToHaveTextAsync(reordered);

            await page.ReloadAsync();
            await Assertions.Expect(rows.Locator(".resource-row-name")).ToHaveTextAsync(reordered);
            var movedHandle = pane.Locator($"[data-resource-name='{originalOrder[2]}'] [data-resource-drag-handle]");
            await movedHandle.FocusAsync();
            await movedHandle.PressAsync("Alt+ArrowDown");
            await Assertions.Expect(rows.Locator(".resource-row-name")).ToHaveTextAsync([originalOrder[0], originalOrder[2], originalOrder[1]]);
            await Assertions.Expect(movedHandle).ToBeFocusedAsync();
            await movedHandle.PressAsync("Alt+ArrowDown");
            await Assertions.Expect(rows.Locator(".resource-row-name")).ToHaveTextAsync(originalOrder);
            await Assertions.Expect(movedHandle).ToBeFocusedAsync();

            var search = pane.GetByRole(AriaRole.Searchbox);
            await search.FillAsync("api");
            await Assertions.Expect(pane.Locator("[data-resource-drag-handle]")).ToHaveCountAsync(0);
            await search.FillAsync(string.Empty);
            await Assertions.Expect(pane.Locator("[data-resource-drag-handle]")).ToHaveCountAsync(3);

            await page.Locator("#resourcePaneGroupingButton").ClickAsync();
            await page.GetByRole(AriaRole.Menuitemradio, new PageGetByRoleOptions { Name = Dashboard.Resources.Layout.ResourcePaneGroupingState, Exact = true }).ClickAsync();
            await Assertions.Expect(pane.Locator("[data-resource-drag-handle]")).ToHaveCountAsync(0);
            await page.Locator("#resourcePaneGroupingButton").ClickAsync();
            await page.GetByRole(AriaRole.Menuitemradio, new PageGetByRoleOptions { Name = Dashboard.Resources.Layout.ResourcePaneGroupingNone, Exact = true }).ClickAsync();
            await Assertions.Expect(rows.Locator(".resource-row-name")).ToHaveTextAsync(originalOrder);
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ViewOptionsMenu_ReportsExpandedState()
    {
        await RunTestAsync(async page =>
        {
            await GoToParametersAndWaitForDataGridLoadAsync(page).DefaultTimeout();

            var viewOptionsButton = page.GetByRole(AriaRole.Button, new() { Name = Dashboard.Resources.Resources.ResourcesChangeViewOptions, Exact = true });
            await Assertions.Expect(viewOptionsButton).Not.ToHaveAttributeAsync("aria-expanded", "true");

            await viewOptionsButton.ClickAsync();
            await Assertions.Expect(viewOptionsButton).ToHaveAttributeAsync("aria-expanded", "true");

            await Assertions.Expect(page.GetByRole(AriaRole.Menuitem, new PageGetByRoleOptions { Name = Dashboard.Resources.Resources.ResourceCollapseAllChildren, Exact = true })).ToHaveCountAsync(0);
            await Assertions.Expect(page.GetByRole(AriaRole.Menuitem, new PageGetByRoleOptions { Name = Dashboard.Resources.Resources.ResourceExpandAllChildren, Exact = true })).ToHaveCountAsync(0);
            var showResourceTypes = page.GetByRole(AriaRole.Menuitem, new PageGetByRoleOptions { Name = Dashboard.Resources.Resources.ResourcesShowTypes, Exact = true });
            await showResourceTypes.ClickAsync();
            await Assertions.Expect(viewOptionsButton).Not.ToHaveAttributeAsync("aria-expanded", "true");
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task GridActionButtons_UseCompactMinimumWidth()
    {
        await RunTestAsync(async page =>
        {
            await GoToParametersAndWaitForDataGridLoadAsync(page).DefaultTimeout();

            var buttons = page.Locator(".grid-action-container fluent-button");
            await Assertions.Expect(buttons.First).ToHaveCSSAsync("min-width", "32px");
            var values = await buttons.EvaluateAllAsync<double[]>(
                """
                buttons => buttons.map(button => Number.parseFloat(getComputedStyle(button).minWidth))
                """);

            Assert.NotEmpty(values);
            Assert.Single(values.Distinct());
            Assert.InRange(values[0], 32, 36);
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task NameColumn_SortsParameters()
    {
        await RunTestAsync(async page =>
        {
            await GoToParametersAndWaitForDataGridLoadAsync(page).DefaultTimeout();

            var resourceNames = page.Locator(".main-grid .resource-row .resource-name-text");
            await Assertions.Expect(resourceNames).ToHaveCountAsync(2);
            await Assertions.Expect(resourceNames.Nth(0)).ToContainTextAsync("alpha-param");
            await Assertions.Expect(resourceNames.Nth(1)).ToContainTextAsync("zeta-param");

            var nameHeader = page.Locator(".main-grid th[col-index='1']");
            var sortItem = page.GetByRole(AriaRole.Menuitem, new PageGetByRoleOptions { Name = ControlsStrings.FluentDataGridHeaderCellSortButtonText, Exact = true });
            var nameHeaderButton = nameHeader.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = ControlsStrings.NameColumnHeader, Exact = true });

            await nameHeaderButton.ClickAsync();
            await sortItem.ClickAsync();
            await Assertions.Expect(nameHeader).ToHaveAttributeAsync("aria-sort", "ascending");
            await Assertions.Expect(resourceNames.Nth(0)).ToContainTextAsync("alpha-param");

            await nameHeaderButton.ClickAsync();
            await page.GetByRole(AriaRole.Menuitem, new PageGetByRoleOptions { Name = ControlsStrings.FluentDataGridHeaderCellSortAscendingButtonText, Exact = true }).ClickAsync();
            await Assertions.Expect(nameHeader).ToHaveAttributeAsync("aria-sort", "descending");
            await Assertions.Expect(resourceNames.Nth(0)).ToContainTextAsync("zeta-param");
            await Assertions.Expect(resourceNames.Nth(1)).ToContainTextAsync("alpha-param");
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ResourcePane_MobileDrawerSelectsResource()
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(360, 720);
            await page.GotoAsync("/resources");

            var layout = page.Locator(".resources-layout");
            await Assertions.Expect(layout).Not.ToHaveClassAsync(new Regex("drawer-open"));

            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = Dashboard.Resources.Layout.ResourceHeaderAllResources, Exact = true }).ClickAsync();
            await Assertions.Expect(layout).ToHaveClassAsync(new Regex("drawer-open"));

            var resourceLink = page.Locator(".resource-pane").GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = "TestResource" });
            var linkBounds = await resourceLink.BoundingBoxAsync();
            Assert.NotNull(linkBounds);
            Assert.True(linkBounds.X >= 0 && linkBounds.X + linkBounds.Width <= 360, $"The resource link should fit inside the viewport, but it spanned {linkBounds.X} to {linkBounds.X + linkBounds.Width}.");

            await page.Mouse.ClickAsync(
                linkBounds.X + linkBounds.Width / 2,
                linkBounds.Y + linkBounds.Height / 2);
            await page.WaitForFunctionAsync("() => location.pathname === '/resources/TestResource'");
            await Assertions.Expect(layout).Not.ToHaveClassAsync(new Regex("drawer-open"));
            await Assertions.Expect(page.Locator(".resource-title")).ToHaveTextAsync("TestResource");
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

            var layoutSelector = page.GetByRole(AriaRole.Group, new PageGetByRoleOptions { Name = Dashboard.Resources.Resources.LayoutLabel, Exact = true });
            var graphButton = layoutSelector.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = Dashboard.Resources.Layout.NavMenuGraphTab, Exact = true });
            await graphButton.ClickAsync();
            await Assertions.Expect(graphButton).ToHaveAttributeAsync("aria-pressed", "true");

            var graphContainer = page.GetByRole(AriaRole.Region, new() { Name = Dashboard.Resources.ControlsStrings.ChartContainerGraphAccessibleLabel });
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
            await page.GetByRole(AriaRole.Group, new() { Name = Dashboard.Resources.Resources.LayoutLabel, Exact = true })
                .GetByRole(AriaRole.Button, new() { Name = Dashboard.Resources.Layout.NavMenuGraphTab, Exact = true })
                .ClickAsync();

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
            await WaitForPositionToStabilizeAsync(node);
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

            await Assertions.Expect(page.Locator("fluent-menu-list:visible")).ToHaveCountAsync(0);

            await node.HoverAsync();
            await cog.ClickAsync();
            var menu = page.Locator("fluent-menu-list:visible");
            await Assertions.Expect(menu).ToBeVisibleAsync();
            await Assertions.Expect(cog).ToHaveAttributeAsync("aria-expanded", "true");
            Assert.False((await node.GetAttributeAsync("class"))?.Split(' ').Contains("resource-group-selected"));

            await menu.GetByRole(AriaRole.Menuitem).First.FocusAsync();
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
            Assert.InRange(headerBounds.Height, 35, 37);

            await menu.GetByRole(AriaRole.Menuitem).First.FocusAsync();
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(menu).ToBeHiddenAsync();
            await Assertions.Expect(cog).ToHaveAttributeAsync("aria-expanded", "false");
            await Assertions.Expect(cog).ToBeFocusedAsync();

            await page.Keyboard.PressAsync("Enter");
            var viewDetails = page.GetByRole(
                AriaRole.Menuitem,
                new PageGetByRoleOptions
                {
                    Name = ControlsStrings.ActionViewDetailsText,
                    Exact = true
                });
            await viewDetails.FocusAsync();
            await page.Keyboard.PressAsync("Enter");

            // Non-parameter resources open in the Resources view rather than a graph side panel.
            await page.WaitForFunctionAsync("() => location.pathname === '/resources/TestResource'");
            await Assertions.Expect(page.Locator(".resource-title")).ToHaveTextAsync("TestResource");
        });
    }

    private static async Task WaitForPositionToStabilizeAsync(ILocator locator)
    {
        await locator.EvaluateAsync(
            """
            element => new Promise(resolve => {
                let previousBounds;
                let stableFrames = 0;

                const checkPosition = () => {
                    const bounds = element.getBoundingClientRect();
                    if (previousBounds &&
                        Math.abs(bounds.x - previousBounds.x) <= 0.5 &&
                        Math.abs(bounds.y - previousBounds.y) <= 0.5) {
                        stableFrames++;
                    } else {
                        stableFrames = 0;
                    }

                    if (stableFrames >= 3) {
                        resolve();
                        return;
                    }

                    previousBounds = bounds;
                    requestAnimationFrame(checkPosition);
                };

                requestAnimationFrame(checkPosition);
            })
            """).DefaultTimeout();
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task GraphNode_RightClickOpensMenuAtCursorWithoutOverlay()
    {
        await RunTestAsync(async page =>
        {
            await PlaywrightFixture.GoToHomeAndWaitForDataGridLoad(page).DefaultTimeout();
            await page.GetByRole(AriaRole.Group, new() { Name = Dashboard.Resources.Resources.LayoutLabel, Exact = true })
                .GetByRole(AriaRole.Button, new() { Name = Dashboard.Resources.Layout.NavMenuGraphTab, Exact = true })
                .ClickAsync();

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

            var menu = page.Locator("fluent-menu-list:visible");
            await Assertions.Expect(menu).ToBeVisibleAsync();
            var menuBounds = await menu.BoundingBoxAsync();
            Assert.NotNull(menuBounds);

            Assert.InRange(Math.Abs(menuBounds.X - cursorX), 0, 2);
            Assert.InRange(Math.Abs(menuBounds.Y - cursorY), 0, 2);

            var visibleBlockingIndicators = await page.Locator("fluent-overlay, .aspire-progress-ring").EvaluateAllAsync<int>(
                "elements => elements.filter(element => element.getBoundingClientRect().width > 0 && element.getBoundingClientRect().height > 0).length");
            Assert.Equal(0, visibleBlockingIndicators);

            await menu.GetByRole(AriaRole.Menuitem).First.FocusAsync();
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
                resourceName: "hidden-resource",
                resourceType: KnownResourceTypes.Container,
                state: KnownResourceState.Running,
                hidden: true),
            ModelTestHelpers.CreateResource(
                resourceName: "TestResource",
                resourceType: KnownResourceTypes.Project,
                state: KnownResourceState.Running,
                urls:
                [
                    new UrlViewModel("http", new Uri("about:blank#resource-url"), isInternal: false, isInactive: false, UrlDisplayPropertiesViewModel.Empty)
                ]),
            ModelTestHelpers.CreateResource(
                resourceName: "zeta-param",
                resourceType: KnownResourceTypes.Parameter,
                state: KnownResourceState.Running),
            ModelTestHelpers.CreateResource(
                resourceName: "alpha-param",
                resourceType: KnownResourceTypes.Parameter,
                state: KnownResourceState.Running)
        ];
    }

    private async Task GoToParametersAndWaitForDataGridLoadAsync(IPage page)
    {
        var baseUrl = DashboardServerFixture.DashboardApp.FrontendSingleEndPointAccessor().GetResolvedAddress();
        await page.Context.AddCookiesAsync([new Cookie { Name = "Aspire_Dashboard_Experience", Value = "legacy", Url = baseUrl }]);
        await page.GotoAsync("/legacy/resources");
        await page.GetByRole(AriaRole.Tab, new() { Name = Dashboard.Resources.Layout.NavMenuParametersTab, Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator(".main-grid .resource-row").First).ToBeVisibleAsync();
    }
}
