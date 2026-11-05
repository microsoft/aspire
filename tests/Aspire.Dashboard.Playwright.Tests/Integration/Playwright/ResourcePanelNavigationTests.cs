// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;
using Aspire.Dashboard.Model;
using Aspire.TestUtilities;
using Aspire.Tests.Shared.DashboardModel;
using Microsoft.Playwright;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration.Playwright;

[RequiresFeature(TestFeature.Playwright)]
public sealed class ResourcePanelNavigationTests(ResourcePanelNavigationTests.NavigationResourcesFixture fixture)
    : PlaywrightTestsBase<ResourcePanelNavigationTests.NavigationResourcesFixture>(fixture)
{
    [Theory]
    [InlineData(320, 740)]
    [InlineData(390, 844)]
    [InlineData(768, 1024)]
    [InlineData(769, 1024)]
    [InlineData(1024, 768)]
    [InlineData(1440, 900)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ResourceNavigation_CloseOrDropdownReturnsToListAndSelectAllReopensContent(int width, int height)
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(width, height);
            await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
            await page.GotoAsync("/resources/tag-a");
            var pane = page.Locator(".resource-pane");
            var content = page.Locator(".resource-content");
            var close = page.Locator(".detail-page-close");
            var picker = page.Locator(".resource-picker");
            var row = pane.Locator("[data-resource-name='tag-b'] .resource-row-link");

            if (width <= 768)
            {
                await Assertions.Expect(close).ToHaveCountAsync(0);
                await Assertions.Expect(picker).ToBeVisibleAsync();
                await picker.ClickAsync();
                await Assertions.Expect(row).ToBeVisibleAsync();
                await Assertions.Expect(content).Not.ToBeVisibleAsync();
                await Assertions.Expect(content).ToHaveAttributeAsync("inert", "");
                await pane.GetByRole(AriaRole.Button, new() { Name = Resources.Layout.ResourcePaneClose, Exact = true }).ClickAsync();
                await Assertions.Expect(picker).ToBeVisibleAsync();
                await Assertions.Expect(content).ToBeVisibleAsync();
                await picker.ClickAsync();
            }
            else
            {
                await Assertions.Expect(close).ToBeVisibleAsync();
                await Assertions.Expect(picker).ToHaveCountAsync(0);
                await close.ClickAsync();
                await Assertions.Expect(page.Locator(".pane-full-canvas")).ToBeVisibleAsync();
                await Assertions.Expect(content).Not.ToBeVisibleAsync();
            }

            await Assertions.Expect(page.Locator(".resource-pane-canvas-toggle")).ToHaveCountAsync(0);
            await row.ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/resources/tag-b$"));
            await Assertions.Expect(content).ToBeVisibleAsync();
            await Assertions.Expect(content).Not.ToHaveAttributeAsync("inert", "");

            if (width <= 768)
            {
                await picker.ClickAsync();
            }
            else
            {
                await close.ClickAsync();
            }
            await pane.Locator(".resource-select-all-toggle").ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/resources$"));
            await Assertions.Expect(content).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".pane-full-canvas,.drawer-open")).ToHaveCountAsync(0);
            await Assertions.Expect(close).ToHaveCountAsync(width <= 768 ? 0 : 1);
            await Assertions.Expect(page.Locator("#blazor-error-ui")).Not.ToBeVisibleAsync();
        });
    }

    [Theory]
    [InlineData(320, 740)]
    [InlineData(390, 844)]
    [InlineData(768, 1024)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ResourceDrawer_ScrollsOnlyListAndKeepsFiltersAndFooterAvailable(int width, int height)
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(width, height);
            await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
            await page.GotoAsync("/resources/tag-a");
            await page.Locator(".resource-picker").ClickAsync();
            var list = page.Locator(".resource-pane-list");
            await Assertions.Expect(list.Locator("[data-resource-name='tag-a']").First).ToBeVisibleAsync();
            var header = page.Locator(".resource-pane-head");
            var footer = page.Locator(".resource-pane-foot").First;
            var headerBefore = await header.EvaluateAsync<double>("header => header.getBoundingClientRect().top - header.closest('.resource-pane').getBoundingClientRect().top");
            var footerBefore = await footer.EvaluateAsync<double>("footer => footer.closest('.resource-pane').getBoundingClientRect().bottom - footer.getBoundingClientRect().bottom");
            Assert.True(await list.EvaluateAsync<bool>("list => list.scrollHeight > list.clientHeight"));
            await list.EvaluateAsync("list => list.scrollTop = list.scrollHeight");
            Assert.True(await list.EvaluateAsync<bool>("list => list.scrollTop > 0"));
            Assert.Equal(headerBefore, await header.EvaluateAsync<double>("header => header.getBoundingClientRect().top - header.closest('.resource-pane').getBoundingClientRect().top"));
            Assert.Equal(footerBefore, await footer.EvaluateAsync<double>("footer => footer.closest('.resource-pane').getBoundingClientRect().bottom - footer.getBoundingClientRect().bottom"));
            await Assertions.Expect(header.GetByRole(AriaRole.Searchbox)).ToBeVisibleAsync();
            await Assertions.Expect(footer).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".resource-content")).Not.ToBeVisibleAsync();
            var overflowing = await page.Locator(".resource-pane").EvaluateAsync<bool>("pane => pane.scrollWidth > pane.clientWidth");
            Assert.False(overflowing);
            await Assertions.Expect(page.Locator("#blazor-error-ui")).Not.ToBeVisibleAsync();
        });
    }

    [Theory]
    [InlineData("/traces?type=server")]
    [InlineData("/structuredlogs?logLevel=error")]
    [InlineData("/metrics?duration=30")]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task SelectAll_ReopensFilteredContentWithoutChangingUrl(string url)
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(1440, 900);
            await page.GotoAsync(url);
            await ResourcePaneNavigation.ToggleResourceListAsync(page);
            await Assertions.Expect(page.Locator(".pane-full-canvas")).ToBeVisibleAsync();
            await ResourcePaneNavigation.ToggleResourceListAsync(page);
            await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex(System.Text.RegularExpressions.Regex.Escape(url) + "$"));
            await Assertions.Expect(page.Locator(".resource-content")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("#blazor-error-ui")).Not.ToBeVisibleAsync();
        });
    }

    [Theory]
    [InlineData("/traces/detail/missing?resource=tag-a&type=server")]
    [InlineData("/metrics/resource/tag-a/meter/Test")]
    [InlineData("/metrics/resource/tag-a?meter=Test&instrument=Requests&duration=30")]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task RetainedResource_ReopensParentFromFullCanvas(string url)
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(1440, 900);
            await page.GotoAsync(url);
            await ResourcePaneNavigation.ToggleResourceListAsync(page);
            var parentUrl = page.Url;
            await page.Locator(".resource-pane [data-resource-name='tag-a'] .resource-row-link").ClickAsync();
            await Assertions.Expect(page.Locator(".resource-content")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".pane-full-canvas")).ToHaveCountAsync(0);
            await Assertions.Expect(page).ToHaveURLAsync(parentUrl);
            await Assertions.Expect(page.Locator("#blazor-error-ui")).Not.ToBeVisibleAsync();
        });
    }

    [Theory]
    [InlineData("/traces/detail/missing", "/traces", 390)]
    [InlineData("/traces/detail/missing", "/traces", 1440)]
    [InlineData("/metrics?meter=Test&duration=30", "/metrics", 390)]
    [InlineData("/metrics?meter=Test&duration=30", "/metrics", 1440)]
    [InlineData("/metrics?meter=Test&instrument=Requests&duration=30", "/metrics", 390)]
    [InlineData("/metrics?meter=Test&instrument=Requests&duration=30", "/metrics", 1440)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task SelectAll_FromUnscopedDetailNavigatesToTab(string url, string expectedDestination, int width)
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(width, 844);
            await page.GotoAsync(url);
            await Assertions.Expect(page.Locator(".resources-layout")).ToHaveAttributeAsync("data-pane-interactions-ready", "true");
            if (width <= 768)
            {
                await page.Locator(".resource-picker").ClickAsync();
            }

            await page.Locator(".resource-select-all-toggle").ClickAsync();

            await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex(System.Text.RegularExpressions.Regex.Escape(expectedDestination) + "$"));
            await Assertions.Expect(page.Locator(".resource-content")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".pane-full-canvas,.drawer-open")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator("#blazor-error-ui")).Not.ToBeVisibleAsync();
        });
    }

    public sealed class NavigationResourcesFixture : DashboardServerFixture
    {
        protected override IReadOnlyList<ResourceViewModel>? Resources =>
        [
            ModelTestHelpers.CreateResource("tag-a", state: KnownResourceState.Running),
            ModelTestHelpers.CreateResource("tag-b", state: KnownResourceState.Running),
            ModelTestHelpers.CreateResource("tag-c", state: KnownResourceState.Running),
            .. Enumerable.Range(1, 40).Select(i => ModelTestHelpers.CreateResource($"worker-{i:D2}", state: KnownResourceState.Running))
        ];
    }
}
