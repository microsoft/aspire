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
    public async Task GoingHome_UsesKeyboardNavigationOrMobileDrawer(int width, int height)
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
                var home = ResourcePaneNavigation.HomeLink(page);
                await home.FocusAsync();
                await home.PressAsync("Enter");
                await Assertions.Expect(page).ToHaveURLAsync(ResourcePaneNavigation.HomeUrl);
                await Assertions.Expect(page.Locator(".pane-full-canvas")).ToBeVisibleAsync();
                var selectAll = page.Locator(".resource-row-all .resource-row-link");
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
            await ResourcePaneNavigation.GoHomeAsync(page);
            await page.SetViewportSizeAsync(width, height);
            // A narrow layout has no full-page table, so home gives way to the overview of all resources.
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("/resources$"));
            await Assertions.Expect(page.Locator(".pane-full-canvas,.pane-selection-reveal,.pane-motion-enabled")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator(".resource-content")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".resource-content")).Not.ToHaveAttributeAsync("inert", "");
            await Assertions.Expect(page.Locator(".resource-pane")).Not.ToBeVisibleAsync();
            await page.Locator(".resource-picker").ClickAsync();
            await Assertions.Expect(page.Locator(".resource-row-all .resource-row-link")).ToHaveAttributeAsync("aria-current", "page");
            await page.GetByRole(AriaRole.Button, new() { Name = Resources.Layout.ResourcePaneClose, Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".resource-content")).ToBeVisibleAsync();
            await page.SetViewportSizeAsync(1440, 900);
            await Assertions.Expect(page.Locator(".resource-pane-resizer")).ToHaveAttributeAsync("aria-valuenow", "507");
            await Assertions.Expect(page.Locator(".pane-full-canvas")).ToHaveCountAsync(0);
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("/resources$"));
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
            await ResourcePaneNavigation.GoHomeAsync(page);
            await page.WaitForFunctionAsync("""
                () => Math.abs(document.querySelector('.resource-pane').getBoundingClientRect().width -
                    document.querySelector('.resources-layout').getBoundingClientRect().width) <= .5
                """);
            await Assertions.Expect(page.Locator(".resource-content")).Not.ToBeVisibleAsync();
            await Assertions.Expect(navigation).ToBeVisibleAsync();
            // At home no view is shown selected. The overview is kept for the next selection.
            await Assertions.Expect(navigation.Locator("#tab-Overview")).ToHaveAttributeAsync("aria-selected", "false");
            await Assertions.Expect(filter).ToHaveValueAsync("tag-");
            Assert.True(await originalPane.EvaluateAsync<bool>("pane => pane === document.querySelector('.resource-pane')"));
            var paneBox = await pane.BoundingBoxAsync();
            var layoutBox = await page.Locator(".resources-layout").BoundingBoxAsync();
            Assert.NotNull(paneBox);
            Assert.NotNull(layoutBox);
            Assert.InRange(Math.Abs(paneBox.Width - layoutBox.Width), 0, 1);
            await Assertions.Expect(pane.Locator(".resource-row-all .resource-row-link")).ToHaveAttributeAsync("aria-current", "page");

            await pane.Locator(".resource-row-all .resource-row-link").ClickAsync();
            await Assertions.Expect(page.Locator(".resource-content")).ToBeVisibleAsync();
            await Assertions.Expect(navigation).ToBeVisibleAsync();
            await Assertions.Expect(navigation.Locator("#tab-Overview")).ToHaveAttributeAsync("aria-selected", "true");
            await ResourcePaneNavigation.GoHomeAsync(page);
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
            await ResourcePaneNavigation.GoHomeAsync(page);
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

    // The race needs the Console tab in the strip. At narrower widths the brand, as wide as the list, leaves room only
    // for the first tab, and the others are chosen from the overflow menu.
    [Theory]
    [InlineData(1000, false)]
    [InlineData(1000, true)]
    [InlineData(1440, false)]
    [InlineData(1440, true)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task HomeThenTab_OpensTheChosenTabForAllResourcesWithoutBouncingBack(int width, bool useKeyboard)
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(width, 900);
            await page.GotoAsync("/resources/tag-a");
            var navigation = page.Locator(".resource-tabs-nav");
            await Assertions.Expect(page.Locator("h1.resource-title")).ToHaveTextAsync("tag-a");
            var console = navigation.Locator("#tab-Console");
            await Assertions.Expect(console).ToBeVisibleAsync();

            // Choose the tab right after going home, while home may still be loading (a click doesn't wait for
            // navigation). The tab must open for all resources, not for the resource that was selected, whether the
            // choice waits for home or lands on the outgoing tab strip. In the latter case only a real pointer or
            // keyboard activation is honored, never the change events the strip raises as it's torn down. Which case
            // a run hits depends on timing.
            await ResourcePaneNavigation.HomeLink(page).ClickAsync();
            if (useKeyboard)
            {
                await console.FocusAsync();
                await page.Keyboard.PressAsync("Enter");
            }
            else
            {
                await console.ClickAsync();
            }

            await Assertions.Expect(page).ToHaveURLAsync(new Regex("/consolelogs$"));
            await Assertions.Expect(navigation.Locator("#tab-Console")).ToHaveAttributeAsync("aria-selected", "true");

            // A stale event that slipped through would navigate again shortly after, back to the resource that was
            // selected or to the home table. Give it time to show up, then check the page stayed put.
            await page.WaitForTimeoutAsync(1500);
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("/consolelogs$"));
            await Assertions.Expect(page.Locator(".resources-layout.pane-full-canvas")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator("h1.resource-title")).ToHaveTextAsync(Resources.Layout.ResourceHeaderAllResources);
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task Dashboard_OpensOnTheCompactFullPageListWithNoViewSelected()
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(1440, 900);
            await page.GotoAsync("/");
            await Assertions.Expect(page).ToHaveURLAsync(ResourcePaneNavigation.HomeUrl);
            await Assertions.Expect(page.Locator(".resources-layout.pane-full-canvas")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".resource-pane-list.density-compact")).ToBeVisibleAsync();
            var allResources = page.Locator(".resource-row-all .resource-row-link");
            await Assertions.Expect(allResources).ToContainTextAsync(Resources.Layout.ResourceHeaderAllResources);
            await Assertions.Expect(page.Locator(".resource-row-all.selected")).ToHaveCountAsync(1);
            await AssertNoTabSelectedAsync(page);

            // The overview is the view kept for a selection, and choosing it opens it for all resources.
            var overview = page.GetByRole(AriaRole.Tab, new() { Name = Resources.Layout.ResourceTabOverview, Exact = true });
            await overview.ClickAsync();
            await Assertions.Expect(page.Locator(".resources-layout.pane-full-canvas")).ToHaveCountAsync(0);
            await Assertions.Expect(overview).ToHaveAttributeAsync("aria-selected", "true");
            await Assertions.Expect(page.Locator("h1.resource-title")).ToHaveTextAsync(Resources.Layout.ResourceHeaderAllResources);
        }, useDefaultDensity: true);
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task Escape_GoesHomeLikeTheBrandWhenNothingElseUsesIt()
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(1440, 900);
            await page.GotoAsync("/resources/tag-a");
            var fullCanvas = page.Locator(".resources-layout.pane-full-canvas");
            var row = page.Locator(".resource-row[data-resource-name='tag-a'] .resource-row-link");
            var overview = page.GetByRole(AriaRole.Tab, new() { Name = Resources.Layout.ResourceTabOverview, Exact = true });
            await Assertions.Expect(page.Locator(".resources-layout")).ToHaveAttributeAsync("data-pane-interactions-ready", "true");
            await Assertions.Expect(page.Locator(".resources-layout")).ToHaveAttributeAsync("data-escape-home", "true");

            // A text field keeps Escape.
            await page.Locator(".resource-pane").GetByRole(AriaRole.Searchbox).FocusAsync();
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("/resources/tag-a$"));

            // So does an open menu, which closes.
            await page.Locator("#resourcePaneGroupingButton").ClickAsync();
            var noGrouping = page.GetByRole(AriaRole.Menuitemradio, new() { Name = "None", Exact = true });
            await Assertions.Expect(noGrouping).ToBeVisibleAsync();
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(noGrouping).ToBeHiddenAsync();
            // The menu closes in the browser, and its button reports it closed once the server renders it. Until then,
            // Escape still counts as used by the menu.
            await Assertions.Expect(page.Locator("#resourcePaneGroupingButton")).Not.ToHaveAttributeAsync("aria-expanded", "true");
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("/resources/tag-a$"));
            await Assertions.Expect(fullCanvas).ToHaveCountAsync(0);

            // Otherwise it does what the brand does: the table of every resource, at home.
            await row.FocusAsync();
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(page).ToHaveURLAsync(ResourcePaneNavigation.HomeUrl);
            await Assertions.Expect(fullCanvas).ToBeVisibleAsync();
            await AssertNoTabSelectedAsync(page);
            await Assertions.Expect(page.Locator(".resources-layout")).Not.ToHaveAttributeAsync("data-escape-home", "true");

            // The data tabs go home too, as their close button did.
            await page.GotoAsync("/consolelogs/resource/tag-a");
            await Assertions.Expect(page.Locator(".resources-layout")).ToHaveAttributeAsync("data-escape-home", "true");
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(page).ToHaveURLAsync(ResourcePaneNavigation.HomeUrl);
            await Assertions.Expect(fullCanvas).ToBeVisibleAsync();

            // A tab with keyboard focus doesn't get it back in the strip re-created at home, where it would show its
            // focus ring.
            await page.GotoAsync("/resources/tag-a");
            await Assertions.Expect(page.Locator(".resources-layout")).ToHaveAttributeAsync("data-escape-home", "true");
            await overview.FocusAsync();
            await Assertions.Expect(overview).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(page).ToHaveURLAsync(ResourcePaneNavigation.HomeUrl);
            await Assertions.Expect(fullCanvas).ToBeVisibleAsync();
            // Give the strip's focus restore, which runs as the strip is re-created and again on the next frame, time
            // to act before checking it didn't.
            await page.WaitForTimeoutAsync(500);
            await Assertions.Expect(page.Locator(".resources-tab-header fluent-tab:focus")).ToHaveCountAsync(0);
        });
    }

    // At home the active tab is a hidden placeholder, so no visible tab is selected.
    private static Task AssertNoTabSelectedAsync(IPage page) =>
        Assertions.Expect(page.Locator(".resources-tab-header fluent-tab[aria-selected='true']:visible")).ToHaveCountAsync(0);
}
