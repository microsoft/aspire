// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;
using Aspire.Dashboard.Resources;
using Aspire.TestUtilities;
using Aspire.Tests.Shared.DashboardModel;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Playwright;
using System.Text.Json;
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
    public async Task TextVisualizer_NoWrap_KeepsDialogWidthAndShowsHorizontalOverflow()
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(1280, 900);
            await PlaywrightFixture.GoToHomeAndWaitForDataGridLoad(page).DefaultTimeout();

            var row = page.GetByText("LongSourceResource", new PageGetByTextOptions { Exact = true })
                .Locator("xpath=ancestor::*[@role='row']")
                .First;
            await Assertions.Expect(row).ToBeVisibleAsync();

            var sourceCell = row.Locator("td[col-index='4']");
            await sourceCell.HoverAsync();

            var openTextVisualizerButton = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
            {
                Name = Dashboard.Resources.Dialogs.OpenInTextVisualizer,
                Exact = true
            });
            await Assertions.Expect(openTextVisualizerButton).ToBeVisibleAsync();
            await openTextVisualizerButton.ClickAsync();

            var wrapCheckbox = page.Locator("fluent-checkbox.word-wrap-checkbox");
            await Assertions.Expect(wrapCheckbox).ToHaveAttributeAsync("current-checked", "true");
            await wrapCheckbox.ClickAsync();
            await Assertions.Expect(page.Locator(".text-visualizer-container .wrap-log-container")).ToHaveCountAsync(1);

            var metricsJson = await page.EvaluateAsync<string>(@"() => {
                const overflow = document.querySelector('.text-visualizer-container .log-overflow');
                const dialogHost = document.querySelector('fluent-dialog.fluent-dialog-main');
                const dialog = dialogHost?.shadowRoot?.querySelector('[part=""control""]');
                if (overflow) {
                    overflow.scrollLeft = 50;
                }

                if (!dialog) {
                    throw new Error('Could not find the fluent-dialog control part for the text visualizer dialog.');
                }

                return JSON.stringify({
                    overflowScrollLeft: overflow ? overflow.scrollLeft : 0,
                    dialogRight: dialog.getBoundingClientRect().right,
                    viewportWidth: window.innerWidth,
                    documentScrollWidth: document.documentElement.scrollWidth
                });
            }");

            using var metricsDocument = JsonDocument.Parse(metricsJson);
            var metrics = metricsDocument.RootElement;
            var overflowScrollLeft = metrics.GetProperty("overflowScrollLeft").GetDouble();
            var dialogRight = metrics.GetProperty("dialogRight").GetDouble();
            var viewportWidth = metrics.GetProperty("viewportWidth").GetDouble();
            var documentScrollWidth = metrics.GetProperty("documentScrollWidth").GetDouble();

            Assert.True(overflowScrollLeft > 0, "No-wrap should allow horizontal scrolling inside the viewer.");
            Assert.True(dialogRight <= viewportWidth, "Dialog should remain within viewport width when no-wrap is enabled.");
            Assert.True(documentScrollWidth <= viewportWidth, "No-wrap should not cause page-level horizontal overflow.");
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task TextVisualizer_ActionsStayWithinDialogAtNarrowViewport()
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(1280, 900);
            await PlaywrightFixture.GoToHomeAndWaitForDataGridLoad(page).DefaultTimeout();

            var row = page.GetByText("LongSourceResource", new PageGetByTextOptions { Exact = true })
                .Locator("xpath=ancestor::*[@role='row']")
                .First;
            await Assertions.Expect(row).ToBeVisibleAsync();

            var sourceCell = row.Locator("td[col-index='4']");
            await sourceCell.HoverAsync();

            var openTextVisualizerButton = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
            {
                Name = Dashboard.Resources.Dialogs.OpenInTextVisualizer,
                Exact = true
            });
            await Assertions.Expect(openTextVisualizerButton).ToBeVisibleAsync();
            await openTextVisualizerButton.ClickAsync();

            await page.SetViewportSizeAsync(360, 900);

            var wrapCheckbox = page.GetByRole(AriaRole.Checkbox, new PageGetByRoleOptions
            {
                Name = ControlsStrings.GridValueWrapLines,
                Exact = true
            });
            var copyButton = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
            {
                Name = ControlsStrings.GridValueCopyToClipboard,
                Exact = true
            });

            await AssertElementWithinViewportAsync(wrapCheckbox, 360);
            await AssertElementWithinViewportAsync(copyButton, 360);
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
            ,
            ModelTestHelpers.CreateResource(
                resourceName: "LongSourceResource",
                resourceType: KnownResourceTypes.Project,
                state: KnownResourceState.Running,
                properties: new[]
                {
                    new KeyValuePair<string, ResourcePropertyViewModel>(
                        KnownProperties.Project.Path,
                        new ResourcePropertyViewModel(
                            KnownProperties.Project.Path,
                            new Value
                            {
                                StringValue = new string('a', 12000)
                            },
                            isValueSensitive: false,
                            knownProperty: new(KnownProperties.Project.Path, _ => "Path"),
                            sortOrder: 0,
                            displayName: null,
                            isHighlighted: false))
                }.ToDictionary())
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

    private static async Task AssertElementWithinViewportAsync(ILocator element, int viewportWidth)
    {
        await Assertions.Expect(element).ToBeVisibleAsync();

        var elementBounds = await element.BoundingBoxAsync();

        Assert.NotNull(elementBounds);
        Assert.True(elementBounds.X >= 0, $"Element should not overflow the viewport on the left. Element X: {elementBounds.X}.");
        Assert.True(elementBounds.X + elementBounds.Width <= viewportWidth, $"Element should fit inside the {viewportWidth}px viewport. Element right edge: {elementBounds.X + elementBounds.Width}.");
    }
}
