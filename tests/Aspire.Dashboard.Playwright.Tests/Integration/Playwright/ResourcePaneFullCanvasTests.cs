// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
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
    public async Task CloseView_UsesKeyboardNavigationOrMobileDrawer(int width, int height)
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
            var grouping = page.Locator("#resourcePaneGroupingButton");
            if (width <= 768)
            {
                await Assertions.Expect(page.Locator(".detail-page-close")).ToHaveCountAsync(0);
                await grouping.FocusAsync();
                await grouping.PressAsync("Tab");
                var close = page.GetByRole(AriaRole.Button, new() { Name = Resources.Layout.ResourcePaneClose, Exact = true });
                await Assertions.Expect(close).ToBeFocusedAsync();
                await close.PressAsync("Enter");
                await Assertions.Expect(page.Locator(".resource-pane")).Not.ToBeVisibleAsync();
                await Assertions.Expect(page.Locator(".resource-content")).ToBeVisibleAsync();
            }
            else
            {
                var close = page.GetByRole(AriaRole.Button, new() { Name = Resources.ControlsStrings.SummaryDetailsViewCloseView, Exact = true });
                await close.FocusAsync();
                await close.PressAsync("Enter");
                await Assertions.Expect(page.Locator(".pane-full-canvas")).ToBeVisibleAsync();
                var selectAll = page.Locator(".resource-select-all-toggle");
                await selectAll.FocusAsync();
                await selectAll.PressAsync("Enter");
                await Assertions.Expect(page.Locator(".pane-full-canvas")).ToHaveCountAsync(0);
                await Assertions.Expect(page.Locator(".resource-content")).ToBeVisibleAsync();
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
            var close = page.Locator(".detail-page-close");
            await close.ClickAsync();
            await Assertions.Expect(page.Locator(".pane-full-canvas")).ToBeVisibleAsync();
            var url = page.Url;
            await page.SetViewportSizeAsync(width, height);
            await Assertions.Expect(close).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator(".pane-full-canvas,.pane-selection-reveal,.pane-motion-enabled")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator(".resource-content")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".resource-content")).Not.ToHaveAttributeAsync("inert", "");
            await Assertions.Expect(page.Locator(".resource-pane")).Not.ToBeVisibleAsync();
            await page.Locator(".resource-picker").ClickAsync();
            await Assertions.Expect(page.Locator(".resource-select-all-toggle")).ToHaveAttributeAsync("aria-description", Resources.Layout.ResourcePaneAllSelected);
            await page.GetByRole(AriaRole.Button, new() { Name = Resources.Layout.ResourcePaneClose, Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".resource-content")).ToBeVisibleAsync();
            await page.SetViewportSizeAsync(1440, 900);
            await Assertions.Expect(close).ToBeVisibleAsync();
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
            await page.Locator(".detail-page-close").ClickAsync();
            await Assertions.Expect(page.Locator(".resources-layout.pane-full-canvas")).ToBeVisibleAsync();
            await page.WaitForFunctionAsync("""
                () => Math.abs(document.querySelector('.resource-pane').getBoundingClientRect().width -
                    document.querySelector('.resources-layout').getBoundingClientRect().width) <= .5
                """);
            await Assertions.Expect(page.Locator(".resource-content")).Not.ToBeVisibleAsync();
            await Assertions.Expect(navigation).ToBeVisibleAsync();
            // The full-page list is the overview's List layout, so closing the overview keeps the Overview tab selected.
            await Assertions.Expect(navigation.Locator("#tab-Overview")).ToHaveAttributeAsync("aria-selected", "true");
            await Assertions.Expect(filter).ToHaveValueAsync("tag-");
            await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/resources$"));
            Assert.True(await originalPane.EvaluateAsync<bool>("pane => pane === document.querySelector('.resource-pane')"));
            var paneBox = await pane.BoundingBoxAsync();
            var layoutBox = await page.Locator(".resources-layout").BoundingBoxAsync();
            Assert.NotNull(paneBox);
            Assert.NotNull(layoutBox);
            Assert.InRange(Math.Abs(paneBox.Width - layoutBox.Width), 0, 1);
            await Assertions.Expect(pane.Locator(".resource-select-all-toggle")).ToHaveAttributeAsync("aria-description", Resources.Layout.ResourcePaneAllSelected);

            await pane.Locator(".resource-select-all-toggle").ClickAsync();
            await Assertions.Expect(page.Locator(".resource-content")).ToBeVisibleAsync();
            await Assertions.Expect(navigation).ToBeVisibleAsync();
            await Assertions.Expect(navigation.Locator("#tab-Overview")).ToHaveAttributeAsync("aria-selected", "true");
            await page.Locator(".detail-page-close").ClickAsync();
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
    public async Task FullCanvas_KeepsResponsiveNavigationUsableAndNewResourceScope(int width, int height)
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
            await page.Locator(".detail-page-close").ClickAsync();
            await Assertions.Expect(navigation).ToBeVisibleAsync();
            await SelectViewAsync("Console");
            await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/consolelogs$"));
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

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task Dashboard_OpensOnTheCompactFullPageListWithOverviewSelected()
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(1440, 900);
            await page.GotoAsync("/");
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("/resources$"));
            await Assertions.Expect(page.Locator(".resources-layout.pane-full-canvas")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".resource-pane-list.density-compact")).ToBeVisibleAsync();
            var allResources = page.Locator(".resource-select-all-toggle");
            await Assertions.Expect(allResources).ToHaveTextAsync(Resources.Layout.ResourceHeaderAllResources);
            await Assertions.Expect(allResources).ToHaveClassAsync(new Regex(@"\bon\b"));

            // The full-page list is the overview's List layout, so the Overview tab is selected, and choosing it again
            // opens the overview's summary or graph for all resources.
            var overview = page.GetByRole(AriaRole.Tab, new() { Name = Resources.Layout.ResourceTabOverview, Exact = true });
            await Assertions.Expect(overview).ToHaveAttributeAsync("aria-selected", "true");
            await overview.ClickAsync();
            await Assertions.Expect(page.Locator(".resources-layout.pane-full-canvas")).ToHaveCountAsync(0);
            await Assertions.Expect(overview).ToHaveAttributeAsync("aria-selected", "true");
            await Assertions.Expect(page.Locator("h1.resource-title")).ToHaveTextAsync(Resources.Layout.ResourceHeaderAllResources);
        }, useDefaultDensity: true);
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task Escape_ClosesContentLikeTheCloseButtonWhenNothingElseUsesIt()
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(1440, 900);
            await page.GotoAsync("/consolelogs/resource/tag-a");
            var fullCanvas = page.Locator(".resources-layout.pane-full-canvas");
            var close = page.Locator(".detail-page-close");
            var row = page.Locator(".resource-row[data-resource-name='tag-a'] .resource-row-link");
            var console = page.GetByRole(AriaRole.Tab, new() { Name = Resources.Layout.NavMenuConsoleLogsTab, Exact = true });
            await Assertions.Expect(close).ToBeVisibleAsync();

            // A text field keeps Escape.
            await page.Locator(".resource-pane").GetByRole(AriaRole.Searchbox).FocusAsync();
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(close).ToBeVisibleAsync();

            // So does an open menu, which closes.
            await page.Locator("#resourcePaneGroupingButton").ClickAsync();
            var noGrouping = page.GetByRole(AriaRole.Menuitemradio, new() { Name = "None", Exact = true });
            await Assertions.Expect(noGrouping).ToBeVisibleAsync();
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(noGrouping).ToBeHiddenAsync();
            await Assertions.Expect(close).ToBeVisibleAsync();
            await Assertions.Expect(fullCanvas).ToHaveCountAsync(0);

            // Otherwise it does what the close button does: the console of all resources, behind the full-page list.
            await row.FocusAsync();
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("/consolelogs$"));
            await Assertions.Expect(fullCanvas).ToBeVisibleAsync();
            await AssertNoTabSelectedAsync(page);

            // The console is still the current tab, and choosing it opens it again.
            await console.ClickAsync();
            await Assertions.Expect(fullCanvas).ToHaveCountAsync(0);
            await Assertions.Expect(console).ToHaveAttributeAsync("aria-selected", "true");

            // Closed again, selecting a resource opens the view last shown.
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(fullCanvas).ToBeVisibleAsync();
            await row.ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("/consolelogs/resource/tag-a$"));
            await Assertions.Expect(fullCanvas).ToHaveCountAsync(0);
            await Assertions.Expect(console).ToHaveAttributeAsync("aria-selected", "true");
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task Escape_AfterClickingResource_DoesNotShowFocusOnItsRow()
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(1440, 900);
            var fullCanvas = page.Locator(".resources-layout.pane-full-canvas");
            var title = page.Locator("h1.resource-title");
            var link = page.Locator(".resource-row[data-resource-name='tag-b'] .resource-row-link");
            var visibleFocus = page.Locator(".resource-row-link:focus-visible");

            // With the list already beside the content, clicking a resource leaves focus on its link, without showing it.
            await page.GotoAsync("/resources/tag-a");
            await Assertions.Expect(title).ToHaveTextAsync("tag-a");
            await Assertions.Expect(fullCanvas).ToHaveCountAsync(0);
            await link.ClickAsync();
            await Assertions.Expect(title).ToHaveTextAsync("tag-b");
            await Assertions.Expect(link).ToBeFocusedAsync();
            await Assertions.Expect(visibleFocus).ToHaveCountAsync(0);

            // Escape is a key press, which would show that focus in the full-page list as an outline around the row.
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(fullCanvas).ToBeVisibleAsync();
            await Assertions.Expect(visibleFocus).ToHaveCountAsync(0);
            Assert.True(await page.EvaluateAsync<bool>("() => document.activeElement === document.body"));

            // Opened with the keyboard, the resource keeps focus after Escape, and it stays visible.
            await page.GotoAsync("/resources/tag-a");
            await Assertions.Expect(title).ToHaveTextAsync("tag-a");
            await link.FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await Assertions.Expect(title).ToHaveTextAsync("tag-b");
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(fullCanvas).ToBeVisibleAsync();
            await Assertions.Expect(link).ToBeFocusedAsync();
            await Assertions.Expect(visibleFocus).ToHaveCountAsync(1);
        });
    }

    // While the content of a tab other than Overview is closed, the active tab is a hidden placeholder, so no visible
    // tab is selected.
    private static Task AssertNoTabSelectedAsync(IPage page) =>
        Assertions.Expect(page.Locator(".resources-tab-header fluent-tab[aria-selected='true']:visible")).ToHaveCountAsync(0);
}
