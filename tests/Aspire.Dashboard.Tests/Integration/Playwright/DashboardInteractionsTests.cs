// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;
using Aspire.TestUtilities;
using Aspire.Tests.Shared.DashboardModel;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Playwright;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration.Playwright;

// Functional coverage for the net-new interactive behaviors implemented purely in app.js: grid
// column auto-fit (double-click a resize handle) and the floating scroll-to-bottom button for
// large scroll regions. These carry real runtime logic (column/track alignment, overflow/edge
// thresholds) and are coupled to specific markup (".resize-handle", ".continuous-scroll-overflow").
// Scanning resting page state can't catch a regression here, so we drive the interactions and assert
// their DOM effects - which also fails loudly if any of those selectors are renamed out from under the JS.
[RequiresFeature(TestFeature.Playwright)]
public class DashboardInteractionsTests : PlaywrightTestsBase<DashboardInteractionsTests.InteractionsDashboardServerFixture>
{
    public DashboardInteractionsTests(InteractionsDashboardServerFixture dashboardServerFixture)
        : base(dashboardServerFixture)
    {
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task GridColumn_DoubleClickResizeHandle_AutoFitsColumnWidth()
    {
        await RunTestAsync(async page =>
        {
            await GoToResourcesAndWaitAsync(page);

            var grid = page.Locator(".main-grid").First;
            await Assertions.Expect(grid).ToBeVisibleAsync();

            // Guard against a Fluent UI Blazor rename of the resize handle class: the auto-fit
            // double-click handler keys off exactly these selectors, so if neither is present the
            // feature is silently dead and this count assertion surfaces it.
            var handles = page.Locator(".main-grid .resize-handle, .main-grid .col-width-draghandle");
            Assert.True(await handles.CountAsync() > 0, "Expected at least one grid resize handle to be rendered.");

            // Fluent writes the resolved template from GetGridTemplateColumns() to the table's *inline*
            // grid-template-columns, so it is already populated at rest (typically with fr/auto tracks).
            // auto-fit resolves every track to concrete px and rewrites the inline template with the
            // fitted column, so the reliable end-to-end proof is that the inline value *changes* and is
            // now an explicit px template.
            var inlineBefore = await grid.EvaluateAsync<string>("el => el.style.gridTemplateColumns");

            // The auto-fit behavior is a delegated document-level "dblclick" listener that keys off
            // e.target.closest(".resize-handle"). Dispatch the dblclick straight onto the handle element
            // rather than a pixel-precise click: the handle is a thin edge bar whose pointer-events are
            // gated, so a coordinate double-click retargets to the header behind it and never reaches the
            // handler. DispatchEvent fires a real bubbling MouseEvent on the exact element, which bubbles
            // to the document listener exactly as a user double-click on the handle would.
            await handles.First.DispatchEventAsync("dblclick");

            await page.WaitForFunctionAsync(
                @"before => {
                    const g = document.querySelector('.main-grid');
                    if (!g) { return false; }
                    const now = g.style.gridTemplateColumns;
                    return now.length > 0 && now !== before && /px/.test(now);
                }",
                inlineBefore)
                .DefaultTimeout();
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ScrollButtons_ActivateForOverflowingRegion_AndScrollIt()
    {
        await RunTestAsync(async page =>
        {
            await GoToResourcesAndWaitAsync(page);

            // The mock dashboard client serves no console-log/telemetry stream, so no page naturally
            // renders an overflowing ".continuous-scroll-overflow" (that class lives on Console logs,
            // Traces and Structured logs). Inject a representative region using the same contract
            // selector the feature discovers, so we can exercise the button feature's real logic -
            // MutationObserver discovery, overflow-threshold activation (240px), edge-threshold
            // visibility (120px) and click-to-scroll - end to end. There are no other scroll regions
            // on the Resources page, so the single ".scroll-buttons" root belongs to this region.
            await page.EvaluateAsync(@"() => {
                const region = document.createElement('div');
                region.className = 'continuous-scroll-overflow';
                region.id = 'synthetic-scroll-region';
                region.style.cssText = 'position:fixed;left:0;top:0;width:400px;height:300px;overflow:auto;z-index:1;';
                const tall = document.createElement('div');
                tall.style.height = '2000px';
                region.appendChild(tall);
                document.body.appendChild(region);
            }");

            var buttons = page.Locator(".scroll-buttons").First;
            var bottomButton = page.Locator(".scroll-button.scroll-to-bottom").First;

            // Overflow (2000 - 300 = 1700px) is well past the 240px activation threshold.
            await Assertions.Expect(buttons).ToHaveClassAsync(new Regex(@"\bis-active\b"));
            await Assertions.Expect(page.Locator(".scroll-button.scroll-to-top")).ToHaveCountAsync(0);
            await Assertions.Expect(bottomButton).ToHaveClassAsync(new Regex(@"\bis-visible\b"));

            // The buttons are proximity-gated (review feedback made them on-demand rather than always-on):
            // a candidate button carries .is-visible but only actually shows while the pointer is near the
            // region, when JS toggles .is-hovered on the .scroll-buttons group. Hover the region to reveal
            // them - this both satisfies Playwright's actionability check for the click below and asserts
            // the reveal works. The 200ms hide delay is cancelled as the click moves the pointer onto the
            // button (its own pointerenter fires), so the button stays actionable through the click.
            await page.Locator("#synthetic-scroll-region").HoverAsync();
            await Assertions.Expect(bottomButton).ToBeVisibleAsync();

            await bottomButton.ClickAsync();

            // Clicking jumps the region toward the bottom (smooth scroll; poll for scrollTop to move
            // well past the edge threshold).
            await page.WaitForFunctionAsync(
                "() => { const r = document.getElementById('synthetic-scroll-region'); return !!r && r.scrollTop > 500; }")
                .DefaultTimeout();

            // The affordance disappears once the region is already near the bottom.
            await Assertions.Expect(bottomButton).Not.ToHaveClassAsync(new Regex(@"\bis-visible\b"));
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ScrollButtons_RemainInactiveWhenRegionHasNoRoomForControl()
    {
        await RunTestAsync(async page =>
        {
            await GoToResourcesAndWaitAsync(page);

            await page.EvaluateAsync("""
                () => {
                    const region = document.createElement('div');
                    region.className = 'continuous-scroll-overflow';
                    region.id = 'partially-visible-scroll-region';
                    region.style.cssText = 'position:fixed;left:0;top:-280px;width:400px;height:300px;overflow:auto;';
                    const tall = document.createElement('div');
                    tall.style.height = '2000px';
                    region.appendChild(tall);
                    document.body.appendChild(region);
                }
                """);

            var buttons = page.Locator(".scroll-buttons");
            await Assertions.Expect(buttons).ToHaveCountAsync(1);
            await page.EvaluateAsync("() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))").DefaultTimeout();

            Assert.Equal("scroll-buttons", await buttons.GetAttributeAsync("class"));

            await page.EvaluateAsync("""
                () => {
                    document.getElementById('partially-visible-scroll-region').style.top = '0';
                    window.dispatchEvent(new Event('resize'));
                }
                """);
            await page.EvaluateAsync("() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))").DefaultTimeout();

            Assert.Equal("scroll-buttons is-active", await buttons.GetAttributeAsync("class"));
        });
    }

    private static async Task GoToResourcesAndWaitAsync(IPage page)
    {
        await page.GotoAsync("/");
        await Assertions
            .Expect(page.GetByText(InteractionsDashboardServerFixture.ParentResourceName).First)
            .ToBeVisibleAsync();
    }

    public sealed class InteractionsDashboardServerFixture : DashboardServerFixture
    {
        public const string ParentResourceName = "parentapp";

        protected override IReadOnlyList<ResourceViewModel> Resources =>
        [
            ModelTestHelpers.CreateResource(
                resourceName: ParentResourceName,
                resourceType: KnownResourceTypes.Project,
                state: KnownResourceState.Running,
                urls:
                [
                    new UrlViewModel("http", new Uri("about:blank#parent-url"), isInternal: false, isInactive: false, UrlDisplayPropertiesViewModel.Empty)
                ]),
        ];
    }
}
