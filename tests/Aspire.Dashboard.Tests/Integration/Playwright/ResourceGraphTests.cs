// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.RegularExpressions;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.ResourceGraph;
using Aspire.Dashboard.Resources;
using Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;
using Aspire.TestUtilities;
using Aspire.Tests.Shared.DashboardModel;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration.Playwright;

// These focused browser checks share an in-process dashboard and need no AppHost or containers.
[RequiresFeature(TestFeature.Playwright)]
public class ResourceGraphTests(ResourceGraphTests.GraphDashboardServerFixture fixture)
    : PlaywrightTestsBase<ResourceGraphTests.GraphDashboardServerFixture>(fixture)
{
    [Fact]
    public async Task Drag_ContinuesAcrossHealthUpdates_AndStaysPinned()
    {
        await RunTestAsync(async page =>
        {
            await OpenGraphAsync(page);
            var node = page.Locator(".resource-group[resource-name='healthy']");
            var circle = node.Locator(".resource-node");
            var bounds = await circle.BoundingBoxAsync();
            Assert.NotNull(bounds);
            var startX = bounds.X + bounds.Width / 2;
            var startY = bounds.Y + bounds.Height / 2;

            await page.Mouse.MoveAsync(startX, startY);
            await page.Mouse.DownAsync();
            try
            {
                await page.Mouse.MoveAsync(startX + 15, startY + 15, new MouseMoveOptions { Steps = 3 });
                await UpdateResourcesAsync(page, HealthStatus.Healthy);
                await page.Mouse.MoveAsync(startX + 90, startY + 60, new MouseMoveOptions { Steps = 5 });
            }
            finally
            {
                await page.Mouse.UpAsync();
            }

            await Assertions.Expect(node).ToHaveClassAsync(new Regex(@"\bresource-group-pinned\b"));
            var dropped = await circle.BoundingBoxAsync();
            Assert.NotNull(dropped);
            Assert.InRange(Math.Abs(dropped.X + dropped.Width / 2 - startX - 90), 0, 2);
            Assert.InRange(Math.Abs(dropped.Y + dropped.Height / 2 - startY - 60), 0, 2);

            var position = await node.EvaluateAsync<double[]>("element => [element.__data__.fx, element.__data__.fy]");
            await UpdateResourcesAsync(page, HealthStatus.Unhealthy);
            Assert.Equal(position, await node.EvaluateAsync<double[]>("element => [element.__data__.fx, element.__data__.fy]"));
            await Assertions.Expect(node).ToHaveClassAsync(new Regex(@"\bresource-group-pinned\b"));
        });
    }

    [Fact]
    public async Task DropOnPinnedNode_ReleasesTheOlderPin_AndSeparatesTheNodes()
    {
        await RunTestAsync(async page =>
        {
            await OpenGraphAsync(page);
            var older = page.Locator(".resource-group[resource-name='worker-1']");
            var newer = page.Locator(".resource-group[resource-name='worker-2']");
            var original = await older.Locator(".resource-node").BoundingBoxAsync();
            Assert.NotNull(original);
            await DragToAsync(page, older, original.X + original.Width / 2 + 30, original.Y + original.Height / 2 + 30);

            var destination = await older.Locator(".resource-node").BoundingBoxAsync();
            Assert.NotNull(destination);
            await DragToAsync(page, newer, destination.X + destination.Width / 2, destination.Y + destination.Height / 2);

            await Assertions.Expect(newer).ToHaveClassAsync(new Regex(@"\bresource-group-pinned\b"));
            Assert.False(await older.EvaluateAsync<bool>("element => !!element.__data__.pinned"));
            await page.WaitForFunctionAsync(
                """
                () => {
                    const a = document.querySelector("[resource-name='worker-1']").__data__;
                    const b = document.querySelector("[resource-name='worker-2']").__data__;
                    return Math.hypot(a.x - b.x, a.y - b.y) >= 180;
                }
                """);
        });
    }

    [Theory]
    [InlineData("database", "Unhealthy")]
    [InlineData("cache", "Degraded")]
    [InlineData("healthy", "Healthy")]
    public async Task HealthColors_RemainVisibleDuringHoverAndSelection(string resourceName, string state)
    {
        await RunTestAsync(async page =>
        {
            await OpenGraphAsync(page);
            var node = page.Locator($".resource-group[resource-name='{resourceName}']");
            var circle = node.Locator(".resource-node");
            await Assertions.Expect(node).ToHaveAttributeAsync("data-health", state);
            var fill = await circle.EvaluateAsync<string>("element => getComputedStyle(element).fill");

            await node.Locator(".resource-scale").HoverAsync();
            await Assertions.Expect(circle).ToHaveCSSAsync("fill", fill);
            await Assertions.Expect(page.Locator($".links line[data-health='{state}']").First)
                .ToHaveCSSAsync("stroke-dasharray", "none");

            await node.Locator(".resource-scale").ClickAsync();
            await Assertions.Expect(node).ToHaveClassAsync(new Regex(@"\bresource-group-selected\b"));
            await Assertions.Expect(circle).ToHaveCSSAsync("fill", fill);
        });
    }

    [Fact]
    public async Task Reset_AfterZoomAndPan_FitsTheWholeGraph()
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(700, 650);
            await OpenGraphAsync(page);
            var node = page.Locator(".resource-group[resource-name='healthy']");
            var bounds = await node.Locator(".resource-node").BoundingBoxAsync();
            Assert.NotNull(bounds);
            await DragToAsync(page, node, bounds.X + bounds.Width / 2 + 50, bounds.Y + bounds.Height / 2 + 40);
            await Assertions.Expect(node).ToHaveClassAsync(new Regex(@"\bresource-group-pinned\b"));
            await page.Locator(".graph-zoom-in").ClickAsync();
            await page.Mouse.MoveAsync(300, 200);
            await page.Mouse.WheelAsync(0, -500);
            await page.Locator(".graph-reset").ClickAsync();

            await AssertGraphFitsAsync(page);
            Assert.Empty(await page.Locator(".resource-group-pinned").AllAsync());
        });
    }

    [Fact]
    public async Task ZoomButton_PreservesFramingWhenContainerResizes()
    {
        await RunTestAsync(async page =>
        {
            await OpenGraphAsync(page);
            var svg = page.Locator(".resource-graph");
            var initialScale = await svg.EvaluateAsync<double>("element => element.__zoom.k");
            await page.Locator(".graph-zoom-in").ClickAsync();
            await page.WaitForFunctionAsync(
                "scale => document.querySelector('.resource-graph').__zoom.k >= scale * 1.49", initialScale);

            await page.SetViewportSizeAsync(1000, 740);
            await page.WaitForFunctionAsync(
                "() => document.querySelector('.resource-graph').viewBox.baseVal.width === document.querySelector('.resource-graph-container').clientWidth");
            var scaleAfterResize = await svg.EvaluateAsync<double>("element => element.__zoom.k");
            Assert.InRange(scaleAfterResize, initialScale * 1.49, initialScale * 1.51);
        });
    }

    [Fact]
    public async Task AppHost_HasNoResourceCommands()
    {
        await RunTestAsync(async page =>
        {
            await OpenGraphAsync(page);
            var appHost = page.Locator(".resource-group[resource-name='$apphost']");
            await Assertions.Expect(appHost).ToBeVisibleAsync();
            await Assertions.Expect(appHost.Locator(".resource-menu-cog")).ToHaveCountAsync(0);
        });
    }

    [Fact]
    public async Task ResourceActions_RemainKeyboardAccessibleAfterHealthUpdate()
    {
        await RunTestAsync(async page =>
        {
            await OpenGraphAsync(page);
            await UpdateResourcesAsync(page, HealthStatus.Healthy);
            var cog = page.Locator(".resource-group[resource-name='healthy'] .resource-menu-cog");
            await cog.FocusAsync();
            await page.Keyboard.PressAsync("Enter");

            var menu = page.GetByRole(AriaRole.Menu, new PageGetByRoleOptions { Name = "healthy", Exact = true });
            await Assertions.Expect(menu).ToBeVisibleAsync();
            await Assertions.Expect(cog).ToHaveAttributeAsync("aria-expanded", "true");
            await AsyncTestHelpers.AssertIsTrueRetryAsync(
                async () => await menu.EvaluateAsync<bool>("element => element.contains(document.activeElement)"),
                "The resource menu should receive keyboard focus before Escape is sent.");

            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(menu).ToBeHiddenAsync();
            await Assertions.Expect(cog).ToBeFocusedAsync();
        });
    }

    [Fact]
    public async Task ReenteringGraph_DoesNotAccumulateGraphElements()
    {
        await RunTestAsync(async page =>
        {
            await OpenGraphAsync(page);
            await page.Locator("a[href='/traces']").First.ClickAsync();
            await Assertions.Expect(page.Locator(".resource-graph")).ToHaveCountAsync(0);
            await page.Locator("a[href='/']").First.ClickAsync();
            await page.Locator("#tab-Graph").ClickAsync();
            await Assertions.Expect(page.Locator(".resource-graph > g")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator(".resource-graph > defs")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator(".resource-group")).ToHaveCountAsync(GraphDashboardServerFixture.CreateResources(HealthStatus.Unhealthy).Count + 1);
        });
    }

    [Fact]
    public async Task HyphenatedResourceNames_KeepDistinctRelationshipLines()
    {
        await RunTestAsync(async page =>
        {
            await OpenGraphAsync(page);
            await UpdateResourcesAsync(page,
            [
                ModelTestHelpers.CreateResource("api-cache", state: KnownResourceState.Running,
                    relationships: [new("db", KnownRelationshipTypes.Reference)]),
                ModelTestHelpers.CreateResource("api", state: KnownResourceState.Running,
                    relationships: [new("cache-db", KnownRelationshipTypes.Reference)]),
                ModelTestHelpers.CreateResource("cache-db", state: KnownResourceState.Running),
                ModelTestHelpers.CreateResource("db", state: KnownResourceState.Running)
            ]);
            await Assertions.Expect(page.Locator(".links line")).ToHaveCountAsync(4);
            var keys = await page.Locator(".links line").EvaluateAllAsync<string[]>(
                "elements => elements.map(element => element.__data__.id)");

            Assert.Equal(4, keys.Length);
            Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        });
    }

    private async Task OpenGraphAsync(IPage page)
    {
        page.PageError += (_, error) => TestContext.Current.TestOutputHelper?.WriteLine(error);
        await PlaywrightFixture.GoToResourcesAsync(page);
        await page.Locator("#tab-Graph").ClickAsync();
        await Assertions.Expect(page.Locator(".resource-group"))
            .ToHaveCountAsync(GraphDashboardServerFixture.CreateResources(HealthStatus.Unhealthy).Count + 1);
        await page.WaitForFunctionAsync(
            """
            () => [...document.querySelectorAll('.resource-group')].every(element => {
                const node = element.__data__;
                return Number.isFinite(node.x) && Number.isFinite(node.y) &&
                    Math.abs(node.vx || 0) < 0.05 && Math.abs(node.vy || 0) < 0.05;
            })
            """);
    }

    private static Task AssertGraphFitsAsync(IPage page)
    {
        return page.WaitForFunctionAsync(
            """
            () => {
                const bounds = document.querySelector('.resource-graph-container').getBoundingClientRect();
                return [...document.querySelectorAll('.resource-group')].every(element => {
                    const node = element.getBoundingClientRect();
                    return node.left >= bounds.left - 1 && node.right <= bounds.right + 1 &&
                        node.top >= bounds.top - 1 && node.bottom <= bounds.bottom + 1;
                });
            }
            """);
    }

    private static async Task DragToAsync(IPage page, ILocator node, float x, float y)
    {
        var bounds = await node.Locator(".resource-node").BoundingBoxAsync();
        Assert.NotNull(bounds);
        await page.Mouse.MoveAsync(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        await page.Mouse.DownAsync();
        try
        {
            await page.Mouse.MoveAsync(x, y, new MouseMoveOptions { Steps = 5 });
        }
        finally
        {
            await page.Mouse.UpAsync();
        }
    }

    private static Task UpdateResourcesAsync(IPage page, HealthStatus databaseHealth) =>
        UpdateResourcesAsync(page, GraphDashboardServerFixture.CreateResources(databaseHealth));

    private static async Task UpdateResourcesAsync(IPage page, IReadOnlyList<ResourceViewModel> graphResources)
    {
        var resources = graphResources
            .OrderBy(r => r.ResourceType).ThenBy(r => r.Name).ToList();
        var dtos = ResourceGraphMapper.MapResources(
            resources, resources.ToDictionary(r => r.Name), new TestStringLocalizer<Columns>(),
            showHiddenResources: false, new IconResolver(NullLogger<IconResolver>.Instance), "IntegrationTestApplication");
        var json = JsonSerializer.Serialize(dtos, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        // Import the page's existing module instance to exercise an update between real pointer events.
        await page.EvaluateAsync(
            """
            async json => {
                const graph = await import('/js/app-resourcegraph.js');
                graph.updateResourcesGraph(JSON.parse(json));
            }
            """, json);
    }

    public sealed class GraphDashboardServerFixture : DashboardServerFixture
    {
        protected override IReadOnlyList<ResourceViewModel> Resources => CreateResources(HealthStatus.Unhealthy);

        internal static IReadOnlyList<ResourceViewModel> CreateResources(HealthStatus databaseHealth) =>
        [
            ModelTestHelpers.CreateResource("api", state: KnownResourceState.Running,
                relationships: [new("database", KnownRelationshipTypes.Reference), new("cache", KnownRelationshipTypes.Reference), new("healthy", KnownRelationshipTypes.Reference)]),
            ModelTestHelpers.CreateResource("database", state: KnownResourceState.Running, reportHealthStatus: databaseHealth),
            ModelTestHelpers.CreateResource("cache", state: KnownResourceState.Running, reportHealthStatus: HealthStatus.Degraded),
            ModelTestHelpers.CreateResource("healthy", state: KnownResourceState.Running),
            .. Enumerable.Range(1, 8).Select(i => ModelTestHelpers.CreateResource($"worker-{i}", state: KnownResourceState.Running))
        ];
    }
}
