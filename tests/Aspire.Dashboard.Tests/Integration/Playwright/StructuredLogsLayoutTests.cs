// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;
using Aspire.TestUtilities;
using Microsoft.Playwright;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration.Playwright;

[RequiresFeature(TestFeature.Playwright)]
public sealed class StructuredLogsLayoutTests : PlaywrightTestsBase<DashboardServerFixture>
{
    public StructuredLogsLayoutTests(DashboardServerFixture dashboardServerFixture)
        : base(dashboardServerFixture)
    {
    }

    [Theory]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    [InlineData("/", ".resource-tabs-toolbar")]
    [InlineData("/consolelogs", ".content-layout .main-toolbar")]
    [InlineData("/metrics", ".content-layout .main-toolbar", ".toolbar-select")]
    [InlineData("/traces", ".content-layout .main-toolbar", ".toolbar-select")]
    [InlineData("/structuredlogs", ".main-toolbar.filter-toolbar", ".toolbar-search, .toolbar-select")]
    public async Task FilterToolbar_UsesSharedResponsiveLayout(string url, string toolbarSelector, string? requiredControlSelector = null)
    {
        await RunTestAsync(async page =>
        {
            await page.GotoAsync(url);

            var toolbar = page.Locator(toolbarSelector);
            await Assertions.Expect(toolbar).ToBeVisibleAsync();
            await Assertions.Expect(toolbar).ToHaveClassAsync(new Regex("(?:^|\\s)filter-toolbar(?:\\s|$)"));

            var layout = await toolbar.EvaluateAsync<double[]>(
                """
                toolbar => {
                    const controls = [...toolbar.children]
                        .map(element => element.getBoundingClientRect())
                        .filter(bounds => bounds.width > 0 && bounds.height > 0);
                    const centers = controls.map(bounds => bounds.top + bounds.height / 2);
                    const style = getComputedStyle(toolbar);

                    return [
                        style.display === 'flex' ? 1 : 0,
                        style.flexWrap === 'wrap' ? 1 : 0,
                        style.overflowX === 'visible' ? 1 : 0,
                        centers.length === 0 ? 0 : Math.max(...centers) - Math.min(...centers)
                    ];
                }
                """);

            Assert.Equal(1, layout[0]);
            Assert.Equal(1, layout[1]);
            Assert.Equal(1, layout[2]);
            Assert.InRange(layout[3], 0, 1);

            var search = toolbar.Locator(".toolbar-search");
            if (await search.CountAsync() > 0)
            {
                await Assertions.Expect(search.Locator("fluent-text-input")).ToHaveAttributeAsync("slot", "input");

                var searchWidth = await search.EvaluateAsync<double>("element => element.getBoundingClientRect().width");
                Assert.InRange(searchWidth, 160, 280);

                var searchIcon = search.Locator("svg");
                await Assertions.Expect(searchIcon).ToBeVisibleAsync();
                var iconWidth = await searchIcon.EvaluateAsync<double>("element => element.getBoundingClientRect().width");
                Assert.Equal(16, iconWidth);
            }

            var endGroup = toolbar.Locator(":scope > .toolbar-end");
            await Assertions.Expect(endGroup).ToHaveCountAsync(1);

            var endGroupLayout = await endGroup.EvaluateAsync<double[]>(
                """
                endGroup => {
                    const controls = [...endGroup.children]
                        .map(element => element.getBoundingClientRect())
                        .filter(bounds => bounds.width > 0 && bounds.height > 0);
                    const centers = controls.map(bounds => bounds.top + bounds.height / 2);
                    return [
                        getComputedStyle(endGroup).flexWrap === 'nowrap' ? 1 : 0,
                        centers.length === 0 ? 0 : Math.max(...centers) - Math.min(...centers)
                    ];
                }
                """);
            Assert.Equal(1, endGroupLayout[0]);
            Assert.InRange(endGroupLayout[1], 0, 1);

            var endGroupOffset = await toolbar.EvaluateAsync<double>(
                """
                toolbar => {
                    const toolbarBounds = toolbar.getBoundingClientRect();
                    const endGroupBounds = toolbar.querySelector(':scope > .toolbar-end').getBoundingClientRect();
                    const paddingInlineEnd = parseFloat(getComputedStyle(toolbar).paddingInlineEnd);
                    return toolbarBounds.right - paddingInlineEnd - endGroupBounds.right;
                }
                """);
            Assert.InRange(Math.Abs(endGroupOffset), 0, 1);

            if (requiredControlSelector is not null)
            {
                var requiredControls = toolbar.Locator(requiredControlSelector);
                for (var i = 0; i < await requiredControls.CountAsync(); i++)
                {
                    var requiredControl = requiredControls.Nth(i);
                    await Assertions.Expect(requiredControl).ToBeVisibleAsync();
                    var height = await requiredControl.EvaluateAsync<double>("element => element.getBoundingClientRect().height");
                    Assert.True(height >= 30, $"The required toolbar control should be visible, but its height was {height}px.");
                }
            }
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task FilterToolbar_WrapsAtConstrainedDesktopWidth()
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(800, 720);
            await page.GotoAsync("/structuredlogs");

            var toolbar = page.Locator(".main-toolbar.filter-toolbar");
            await Assertions.Expect(toolbar).ToBeVisibleAsync();

            var layout = await toolbar.EvaluateAsync<double[]>(
                """
                toolbar => {
                    const toolbarBounds = toolbar.getBoundingClientRect();
                    const startControlBounds = toolbar.querySelector('fluent-field.resource-list').getBoundingClientRect();
                    const endGroupBounds = toolbar.querySelector(':scope > .toolbar-end').getBoundingClientRect();
                    const searchBounds = toolbar.querySelector('.toolbar-search').getBoundingClientRect();

                    return [
                        toolbar.scrollWidth - toolbar.clientWidth,
                        endGroupBounds.top - startControlBounds.top,
                        searchBounds.width,
                        toolbarBounds.height
                    ];
                }
                """);

            Assert.InRange(layout[0], 0, 1);
            Assert.True(layout[1] > 30, "The end group should wrap below the leading controls.");
            Assert.InRange(layout[2], 160, 280);
            Assert.True(layout[3] >= 80, $"The wrapped toolbar should use two rows, but its height was {layout[3]}px.");
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ToolbarGridAndFooter_UseFixedPageRows()
    {
        await RunTestAsync(async page =>
        {
            await page.GotoAsync("/structuredlogs");

            var toolbar = page.Locator(".main-toolbar.filter-toolbar");
            await Assertions.Expect(toolbar).ToBeVisibleAsync();

            var controlCenters = await toolbar.EvaluateAsync<double[]>(
                """
                toolbar => [
                    toolbar.querySelector('fluent-field.resource-list'),
                    ...toolbar.querySelectorAll('fluent-button')
                ].map(element => {
                    const bounds = element.getBoundingClientRect();
                    return bounds.top + bounds.height / 2;
                })
                """);

            Assert.NotEmpty(controlCenters);
            Assert.InRange(controlCenters.Max() - controlCenters.Min(), 0, 1);

            var buttonWidths = await toolbar.Locator(":scope > fluent-button").EvaluateAllAsync<double[]>(
                "buttons => buttons.map(button => button.getBoundingClientRect().width)");
            Assert.All(buttonWidths, width => Assert.InRange(width, 32, 72));

            var grid = page.Locator(".logs-grid-container");
            var footer = page.Locator(".content-layout > footer");
            await Assertions.Expect(grid).ToBeVisibleAsync();
            await Assertions.Expect(footer).ToBeVisibleAsync();

            var layout = await page.EvaluateAsync<double[]>(
                """
                () => {
                    const grid = document.querySelector('.logs-grid-container');
                    const footer = document.querySelector('.content-layout > footer');
                    const gridBounds = grid.getBoundingClientRect();
                    const footerBounds = footer.getBoundingClientRect();
                    const overflow = getComputedStyle(grid).overflow;

                    return [
                        overflow === 'auto' ? 1 : 0,
                        gridBounds.bottom,
                        footerBounds.top,
                        footerBounds.bottom,
                        window.innerHeight
                    ];
                }
                """);

            Assert.Equal(1, layout[0]);
            Assert.True(layout[1] <= layout[2] + 1, "The grid must end before the footer begins.");
            Assert.InRange(Math.Abs(layout[3] - layout[4]), 0, 1);
        });
    }
}
