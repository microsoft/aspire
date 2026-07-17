// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Resources;
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
    private const string MultiUrlResourceName = "MultiUrlResource";

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
            await Assertions.Expect(viewOptionsButton).ToHaveAttributeAsync("aria-expanded", "false");

            await viewOptionsButton.ClickAsync();
            await Assertions.Expect(viewOptionsButton).ToHaveAttributeAsync("aria-expanded", "true");

            var showResourceTypes = page.GetByRole(AriaRole.Menuitem, new PageGetByRoleOptions { Name = Dashboard.Resources.Resources.ResourcesShowTypes, Exact = true });
            await showResourceTypes.ClickAsync();
            await Assertions.Expect(viewOptionsButton).ToHaveAttributeAsync("aria-expanded", "false");
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ViewOptionsMenu_TabMovesFocusToNextLogicalControl()
    {
        await RunTestAsync(async page =>
        {
            await PlaywrightFixture.GoToHomeAndWaitForDataGridLoad(page).DefaultTimeout();

            var viewOptions = page.Locator("#resourcesViewOptionsButton");
            await viewOptions.ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Menu)).ToBeVisibleAsync();

            await page.Keyboard.PressAsync("Tab");
            await page.Keyboard.PressAsync("Tab");

            await Assertions.Expect(page.GetByRole(AriaRole.Menu)).ToBeHiddenAsync();
            await AssertActiveElementIsAsync(page, "fluent-tab#tab-Table");
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ResourceFilterPopup_BoundaryTabKeysAndEscapeClosePopup()
    {
        await RunTestAsync(async page =>
        {
            await PlaywrightFixture.GoToHomeAndWaitForDataGridLoad(page).DefaultTimeout();

            var resourceFilter = page.Locator("#resourceFilterButton");
            var popup = page.Locator(".resources-filter-popup");
            var popupCheckboxes = popup.Locator("fluent-checkbox");

            await OpenResourceFilterAsync(resourceFilter, popup);
            await popupCheckboxes.Last.FocusAsync();
            await page.Keyboard.PressAsync("Tab");

            await Assertions.Expect(popup).ToBeHiddenAsync();
            // Tab from the last popup item closes the popup and forwards focus to the next
            // real tabbable element after the anchor in document order. In the resources page
            // that is the data-view-kind tab strip (the "Resources" table tab is the next
            // element with tabindex >= 0; tabindex=-1 elements are correctly skipped by
            // isAspireFocusableElement now that the Fluent-element short-circuit is removed).
            await AssertActiveElementIsAsync(page, "fluent-tab#tab-Table");

            await OpenResourceFilterAsync(resourceFilter, popup);
            await popupCheckboxes.First.FocusAsync();
            await page.Keyboard.PressAsync("Shift+Tab");

            await Assertions.Expect(popup).ToBeHiddenAsync();
            await AssertActiveElementIsAsync(page, "#resourceFilterButton");

            await OpenResourceFilterAsync(resourceFilter, popup);
            await popupCheckboxes.First.FocusAsync();
            await page.Keyboard.PressAsync("Escape");

            await Assertions.Expect(popup).ToBeHiddenAsync();
            await AssertActiveElementIsAsync(page, "#resourceFilterButton");
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
    public async Task UrlOverflowPopover_TabFromTriggerEntersAndShiftTabOrEscapeCloses()
    {
        // The URL overflow popover uses FluentPopover with AutoFocus="false" together with
        // AspirePopupFocusNavigation. This is the same code path used by ChartFilterPopover
        // on the metrics page, so this test also serves as regression coverage for that flow.
        // We can't trigger ChartFilterPopover from the Playwright dashboard fixture because the
        // fixture's MockDashboardClient does not populate the TelemetryRepository.
        await RunTestAsync(async page =>
        {
            await PlaywrightFixture.GoToHomeAndWaitForDataGridLoad(page).DefaultTimeout();

            // The MultiUrlResource has 25 URLs which guarantees UrlsColumnDisplay renders the
            // "+N" overflow button. It is the only resource in the fixture with multiple URLs,
            // so this selector is unique without needing to scope to a specific row.
            var moreButton = page.Locator("fluent-button.url-button").First;
            await Assertions.Expect(moreButton).ToBeVisibleAsync();

            // The popup body is rendered inside FluentPopover with the .url-popup wrapper.
            // FluentPopover keeps the element in the DOM and toggles visibility, so we rely on
            // ToBeVisibleAsync / ToBeHiddenAsync rather than an [open] attribute.
            var popover = page.Locator(".url-popup");
            var firstUrlLink = popover.Locator("a[href]").First;

            await OpenUrlOverflowPopoverAsync(moreButton, popover);

            // Tab from the trigger should land inside the popup (anchor keydown listener path).
            await moreButton.FocusAsync();
            await page.Keyboard.PressAsync("Tab");
            await Assertions.Expect(firstUrlLink).ToBeFocusedAsync();
            await Assertions.Expect(popover).ToBeVisibleAsync();

            // Shift+Tab from the first focusable element closes the popup and returns focus to
            // the trigger button.
            await page.Keyboard.PressAsync("Shift+Tab");
            await Assertions.Expect(popover).ToBeHiddenAsync();
            await Assertions.Expect(moreButton).ToBeFocusedAsync();

            // Reopen, then close with Escape from inside the popup.
            await OpenUrlOverflowPopoverAsync(moreButton, popover);
            await firstUrlLink.FocusAsync();
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(popover).ToBeHiddenAsync();
            await Assertions.Expect(moreButton).ToBeFocusedAsync();

            // The popover must be reopenable after closing - this exercises the
            // dispose-then-reinitialize path in AspirePopupFocusNavigation.OnAfterRenderAsync.
            await OpenUrlOverflowPopoverAsync(moreButton, popover);
            await Assertions.Expect(popover).ToBeVisibleAsync();
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
                hidden: true),
            ModelTestHelpers.CreateResource(
                resourceName: MultiUrlResourceName,
                resourceType: KnownResourceTypes.Project,
                state: KnownResourceState.Running,
                // Use enough URLs (>20) that UrlsColumnDisplay always renders the "+N" more
                // button regardless of viewport width — its preOverflowedCount is non-zero once
                // DisplayedUrls.Count > maxRenderedUrls.
                urls: CreateUrls(25))
        ];

        private static ImmutableArray<UrlViewModel> CreateUrls(int count)
        {
            var builder = ImmutableArray.CreateBuilder<UrlViewModel>(count);
            for (var i = 0; i < count; i++)
            {
                builder.Add(new UrlViewModel(
                    endpointName: $"https-{i}",
                    url: new Uri($"https://localhost:{5000 + i}/"),
                    isInternal: false,
                    isInactive: false,
                    displayProperties: new UrlDisplayPropertiesViewModel($"Endpoint {i}", SortOrder: i)));
            }

            return builder.MoveToImmutable();
        }
    }

    private static async Task OpenResourceFilterAsync(ILocator resourceFilter, ILocator popup)
    {
        await resourceFilter.ClickAsync();
        await Assertions.Expect(popup).ToBeVisibleAsync();
    }

    private static async Task OpenUrlOverflowPopoverAsync(ILocator moreButton, ILocator popover)
    {
        await moreButton.ClickAsync();
        await Assertions.Expect(popover).ToBeVisibleAsync();
    }

    private static async Task AssertActiveElementIsAsync(IPage page, string selector)
    {
        var matches = await page.EvaluateAsync<bool>(
            "selector => document.activeElement === document.querySelector(selector)",
            selector);

        if (!matches)
        {
            var activeElement = await page.EvaluateAsync<string?>(
                """
                () => {
                    const activeElement = document.activeElement;
                    return activeElement
                        ? `${activeElement.tagName.toLowerCase()}#${activeElement.id ?? ""}.${activeElement.className ?? ""}`
                        : null;
                }
                """);

            Assert.True(matches, $"Expected active element to match '{selector}', but active element was '{activeElement}'.");
        }
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
