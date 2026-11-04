// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;
using Aspire.Dashboard.Utils;
using Aspire.TestUtilities;
using Microsoft.Playwright;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration.Playwright;

[RequiresFeature(TestFeature.Playwright)]
public sealed class ResourcePaneFullCanvasTests(ResourceTagPaneTests.TaggedResourcesFixture fixture)
    : PlaywrightTestsBase<ResourceTagPaneTests.TaggedResourcesFixture>(fixture)
{
    [Theory]
    [InlineData(390, 844)]
    [InlineData(768, 1024)]
    [InlineData(769, 1024)]
    [InlineData(1440, 900)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task CanvasToggle_IsDesktopOnlyAndPreservesToolbarKeyboardOrder(int width, int height)
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(width, height);
            await page.GotoAsync("/resources/tag-a");
            if (width <= 768)
            {
                await page.Locator(".resource-picker").ClickAsync();
            }
            await Assertions.Expect(page.Locator(".resource-row[data-resource-name='tag-a']")).ToBeVisibleAsync();
            var toggle = page.Locator(".resource-pane-canvas-toggle");
            var grouping = page.Locator("#resourcePaneGroupingButton");
            await grouping.FocusAsync();
            await grouping.PressAsync("Tab");
            if (width <= 768)
            {
                await Assertions.Expect(toggle).ToHaveCountAsync(0);
                await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = Resources.Layout.ResourcePaneFullCanvas, Exact = true })).ToHaveCountAsync(0);
                await Assertions.Expect(page.Locator($"[data-tooltip='{Resources.Layout.ResourcePaneFullCanvas}']")).ToHaveCountAsync(0);
                var close = page.GetByRole(AriaRole.Button, new() { Name = Resources.Layout.ResourcePaneClose, Exact = true });
                await Assertions.Expect(close).ToBeFocusedAsync();
                await close.PressAsync("Enter");
                await Assertions.Expect(page.Locator(".resource-pane")).Not.ToBeVisibleAsync();
                await Assertions.Expect(page.Locator(".resource-content")).ToBeVisibleAsync();
            }
            else
            {
                await Assertions.Expect(toggle).ToHaveCountAsync(1);
                await Assertions.Expect(toggle).ToBeFocusedAsync();
                await toggle.HoverAsync();
                var tooltip = page.Locator("fluent-tooltip:popover-open").Filter(new() { HasText = Resources.Layout.ResourcePaneFullCanvas });
                await Assertions.Expect(tooltip).ToHaveCountAsync(1);
                await toggle.PressAsync("Escape");
                await toggle.PressAsync("Enter");
                await Assertions.Expect(page.Locator(".pane-full-canvas")).ToBeVisibleAsync();
                await toggle.PressAsync("Enter");
                await Assertions.Expect(page.Locator(".pane-full-canvas")).ToHaveCountAsync(0);
            }
        });
    }

    [Theory]
    [InlineData(390, 844)]
    [InlineData(768, 1024)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task FullCanvas_ResizeToMobileExitsAndDoesNotReenter(int width, int height)
    {
        await RunTestAsync(async page =>
        {
            await page.AddInitScriptAsync($"localStorage.setItem('{BrowserStorageKeys.ResourcePaneWidth}', '507')");
            await page.SetViewportSizeAsync(1440, 900);
            await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
            await page.GotoAsync("/resources/tag-a");
            var toggle = page.Locator(".resource-pane-canvas-toggle");
            await toggle.ClickAsync();
            await Assertions.Expect(page.Locator(".pane-full-canvas")).ToBeVisibleAsync();
            var url = page.Url;
            await page.SetViewportSizeAsync(width, height);
            await Assertions.Expect(toggle).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator(".pane-full-canvas,.pane-selection-reveal,.pane-motion-enabled")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator(".resource-content")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".resource-content")).Not.ToHaveAttributeAsync("inert", "");
            await Assertions.Expect(page.Locator(".resource-pane")).Not.ToBeVisibleAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = Resources.Layout.ResourcePaneExitFullCanvas, Exact = true })).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator("fluent-tooltip:popover-open").Filter(new() { HasText = Resources.Layout.ResourcePaneExitFullCanvas })).ToHaveCountAsync(0);
            await page.Locator(".resource-picker").ClickAsync();
            await Assertions.Expect(page.Locator(".resource-row[data-resource-name='tag-a'] .resource-row-link")).ToHaveAttributeAsync("aria-current", "page");
            await page.GetByRole(AriaRole.Button, new() { Name = Resources.Layout.ResourcePaneClose, Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".resource-content")).ToBeVisibleAsync();
            await page.SetViewportSizeAsync(1440, 900);
            await Assertions.Expect(toggle).ToHaveAttributeAsync("aria-expanded", "false");
            await Assertions.Expect(page.Locator(".pane-full-canvas")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator(".resource-pane-resizer")).ToHaveAttributeAsync("aria-valuenow", "507");
            Assert.Equal(url, page.Url);
            await Assertions.Expect(page.Locator("#blazor-error-ui")).Not.ToBeVisibleAsync();
        });
    }

    [Theory]
    [InlineData(769, 1024)]
    [InlineData(1440, 900)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task FullCanvas_PreservesRealPaneAndReturnsToDetails(int width, int height)
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(width, height);
            await page.GotoAsync("/resources/tag-a");
            await Assertions.Expect(page.Locator("#tab-Overview")).ToHaveAttributeAsync("aria-selected", "true");
            if (width <= 768)
            {
                await page.Locator(".resource-picker").ClickAsync();
            }
            var pane = page.Locator(".resource-pane");
            var navigation = page.Locator(".resource-tabs-nav");
            await Assertions.Expect(navigation).ToBeVisibleAsync();
            var navigationControls = await navigation.Locator("fluent-tab:visible, .resource-tabs-compact-button:visible, .resource-tabs-overflow-button:visible").AllTextContentsAsync();
            Assert.NotEmpty(navigationControls);
            var row = pane.Locator("[data-resource-name='tag-b'] .resource-row-link");
            await Assertions.Expect(row).ToBeVisibleAsync();
            var originalPane = await pane.ElementHandleAsync();
            Assert.NotNull(originalPane);
            var filter = pane.GetByRole(AriaRole.Searchbox);
            await filter.FillAsync("tag-");
            var url = page.Url;
            var canvasToggle = pane.Locator(".resource-pane-canvas-toggle");
            var expandGlyph = await canvasToggle.Locator("svg").InnerHTMLAsync();
            await Assertions.Expect(canvasToggle).ToHaveAttributeAsync("aria-expanded", "false");
            await Assertions.Expect(canvasToggle).ToHaveAttributeAsync("aria-label", Resources.Layout.ResourcePaneFullCanvas);
            var toggleSize = await canvasToggle.EvaluateAsync<double[]>("""
                button => {
                    const b=button.getBoundingClientRect(),s=button.querySelector('svg').getBoundingClientRect();
                    return [b.width,b.height,s.width,s.height];
                }
                """);
            Assert.Equal([32d, 32d, 20d, 20d], toggleSize);
            if (width > 768)
            {
                var centers = await page.EvaluateAsync<double[]>("""
                    () => ['.resource-pane-canvas-toggle','.resource-header-row .resource-pane-toggle-button'].map(selector => {
                        const b=document.querySelector(selector).getBoundingClientRect();
                        return b.y+b.height/2;
                    })
                    """);
                Assert.InRange(Math.Abs(centers[0] - centers[1]), 0, .5);
            }
            await pane.GetByRole(AriaRole.Button, new() { Name = Resources.Layout.ResourcePaneFullCanvas, Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".resources-layout.pane-full-canvas")).ToBeVisibleAsync();
            await page.WaitForFunctionAsync("""
                () => Math.abs(document.querySelector('.resource-pane').getBoundingClientRect().width -
                    document.querySelector('.resources-layout').getBoundingClientRect().width) <= .5
                """);
            await Assertions.Expect(canvasToggle).ToHaveAttributeAsync("aria-expanded", "true");
            await Assertions.Expect(canvasToggle).ToHaveAttributeAsync("aria-label", Resources.Layout.ResourcePaneExitFullCanvas);
            Assert.NotEqual(expandGlyph, await canvasToggle.Locator("svg").InnerHTMLAsync());
            await Assertions.Expect(page.Locator(".resource-content")).Not.ToBeVisibleAsync();
            await Assertions.Expect(navigation).ToBeVisibleAsync();
            Assert.Equal(navigationControls, await navigation.Locator("fluent-tab:visible, .resource-tabs-compact-button:visible, .resource-tabs-overflow-button:visible").AllTextContentsAsync());
            await Assertions.Expect(filter).ToHaveValueAsync("tag-");
            Assert.Equal(url, page.Url);
            Assert.True(await originalPane.EvaluateAsync<bool>("pane => pane === document.querySelector('.resource-pane')"));
            var paneBox = await pane.BoundingBoxAsync();
            var layoutBox = await page.Locator(".resources-layout").BoundingBoxAsync();
            Assert.NotNull(paneBox);
            Assert.NotNull(layoutBox);
            Assert.InRange(Math.Abs(paneBox.Width - layoutBox.Width), 0, 1);
            await Assertions.Expect(pane.Locator("[data-resource-name='tag-a'] .resource-row-link")).ToHaveAttributeAsync("aria-current", "page");

            await pane.GetByRole(AriaRole.Button, new() { Name = Resources.Layout.ResourcePaneExitFullCanvas, Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".resource-content")).ToBeVisibleAsync();
            await Assertions.Expect(navigation).ToBeVisibleAsync();
            Assert.Equal(navigationControls, await navigation.Locator("fluent-tab:visible, .resource-tabs-compact-button:visible, .resource-tabs-overflow-button:visible").AllTextContentsAsync());
            await pane.GetByRole(AriaRole.Button, new() { Name = Resources.Layout.ResourcePaneFullCanvas, Exact = true }).ClickAsync();
            await row.ClickAsync();
            await Assertions.Expect(page.Locator("h1.resource-title")).ToHaveTextAsync("tag-b");
            await Assertions.Expect(page.Locator(".resource-content")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".resources-layout.pane-full-canvas")).ToHaveCountAsync(0);
            if (width > 768)
            {
                await Assertions.Expect(filter).ToHaveValueAsync("tag-");
                await Assertions.Expect(page.Locator(".pane-selection-reveal")).ToHaveCountAsync(0);
                await page.WaitForFunctionAsync("""
                    () => {
                        const layout = document.querySelector('.resources-layout');
                        const width = parseFloat(getComputedStyle(layout).getPropertyValue('--resource-pane-width'));
                        return Math.abs(layout.querySelector('.resource-pane').getBoundingClientRect().width - width) <= .5;
                    }
                    """);
                var compactBox = await pane.BoundingBoxAsync();
                Assert.NotNull(compactBox);
                Assert.True(compactBox.Width < layoutBox.Width);
            }
            else
            {
                await Assertions.Expect(pane).Not.ToBeVisibleAsync();
                await page.Locator(".resource-picker").ClickAsync();
                await Assertions.Expect(filter).ToHaveValueAsync("tag-");
            }
        });
    }

    [Theory]
    [InlineData(769, 1024)]
    [InlineData(1000, 900)]
    [InlineData(1440, 900)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task FullCanvas_KeepsResponsiveNavigationUsableAndResourceScope(int width, int height)
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(width, height);
            await page.GotoAsync("/resources/tag-a");
            var navigation = page.Locator(".resource-tabs-nav");
            await Assertions.Expect(navigation).ToBeVisibleAsync();
            if (width <= 768)
            {
                await page.Locator(".resource-picker").ClickAsync();
            }
            await page.GetByRole(AriaRole.Button, new() { Name = Resources.Layout.ResourcePaneFullCanvas, Exact = true }).ClickAsync();
            await Assertions.Expect(navigation).ToBeVisibleAsync();
            await SelectViewAsync("Console");
            await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/consolelogs/resource/tag-a$"));
            await Assertions.Expect(page.Locator(".resources-layout.pane-full-canvas")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator(".resource-content")).ToBeVisibleAsync();
            if (width <= 768)
            {
                await page.Locator(".resource-picker").ClickAsync();
            }
            await page.Locator("[data-resource-name='tag-b'] .resource-row-link").ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/consolelogs/resource/tag-b$"));
            await SelectViewAsync("Overview");
            await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/resources/tag-b$"));
            await Assertions.Expect(page.Locator("h1.resource-title")).ToHaveTextAsync("tag-b");
            await Assertions.Expect(navigation).ToBeVisibleAsync();

            async Task SelectViewAsync(string view)
            {
                var tab = navigation.Locator($"#tab-{view}");
                if (await tab.IsVisibleAsync())
                {
                    await tab.ClickAsync();
                }
                else
                {
                    var compact = navigation.Locator(".resource-tabs-compact-button");
                    await (await compact.IsVisibleAsync() ? compact : navigation.Locator(".resource-tabs-overflow-button")).ClickAsync();
                    await page.Locator($"#tab-{view}-menu-item").ClickAsync();
                }
            }
        });
    }
}
