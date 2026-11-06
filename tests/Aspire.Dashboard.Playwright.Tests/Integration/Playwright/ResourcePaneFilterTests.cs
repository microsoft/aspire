// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;
using Aspire.TestUtilities;
using Aspire.Tests.Shared.DashboardModel;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Playwright;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration.Playwright;

[RequiresFeature(TestFeature.Playwright)]
public sealed class ResourcePaneFilterTests : PlaywrightTestsBase<ResourcePaneFilterTests.SingleResourceFixture>
{
    public ResourcePaneFilterTests(SingleResourceFixture dashboardServerFixture)
        : base(dashboardServerFixture)
    {
    }

    [Theory]
    [InlineData(390, 844)]
    [InlineData(768, 1024)]
    [InlineData(1440, 900)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task Filter_ContainsActionsAndPreservesSelection(int width, int height)
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(width, height);
            await OpenResourcePaneAsync(page);
            var trigger = page.GetByRole(AriaRole.Button, new() { Name = Resources.ControlsStrings.ChartContainerFiltersHeader, Exact = true });
            await trigger.ClickAsync();
            var filter = page.GetByRole(AriaRole.Group, new() { Name = Resources.ControlsStrings.ChartContainerFiltersHeader, Exact = true });
            await Assertions.Expect(filter).ToBeVisibleAsync();
            await AssertContainedAsync(page, filter);

            var initial = await filter.BoundingBoxAsync();
            Assert.NotNull(initial);
            var project = filter.GetByRole(AriaRole.Checkbox, new() { Name = KnownResourceTypes.Project, Exact = true });
            await project.UncheckAsync();
            await Assertions.Expect(project).Not.ToBeCheckedAsync();
            await Assertions.Expect(page.Locator(".resource-pane .resource-row")).ToHaveCountAsync(0);
            var reset = filter.GetByRole(AriaRole.Button, new() { Name = Resources.Layout.ResourcePaneFilterReset, Exact = true });
            var done = filter.GetByRole(AriaRole.Button, new() { Name = Resources.Layout.ResourcePaneFilterDone, Exact = true });
            await Assertions.Expect(reset).ToBeEnabledAsync();
            await done.ClickAsync();
            await Assertions.Expect(filter).ToBeHiddenAsync();
            await Assertions.Expect(trigger).ToHaveAttributeAsync("aria-expanded", "false");

            await trigger.ClickAsync();
            await Assertions.Expect(project).Not.ToBeCheckedAsync();
            await reset.ClickAsync();
            await Assertions.Expect(page.Locator(".resource-pane .resource-row")).ToHaveCountAsync(1);
            await Assertions.Expect(reset).ToBeDisabledAsync();

            foreach (var header in new[] { Resources.Resources.ResourcesResourceTypesHeader, Resources.Resources.ResourcesResourceStatesHeader })
            {
                await filter.GetByRole(AriaRole.Button, new() { Name = header, Exact = true }).ClickAsync();
            }
            await AsyncTestHelpers.AssertIsTrueRetryAsync(async () => (await filter.BoundingBoxAsync())!.Height < initial.Height,
                "Collapsing sections should shrink the filter instead of retaining a full-height panel.");
            await AssertContainedAsync(page, filter);
            await done.ClickAsync();
            await Assertions.Expect(filter).ToBeHiddenAsync();
            await Assertions.Expect(trigger).ToHaveAttributeAsync("aria-expanded", "false");

            await trigger.ClickAsync();
            await Assertions.Expect(filter).ToBeVisibleAsync();
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(filter).ToBeHiddenAsync();
            await Assertions.Expect(trigger).ToBeFocusedAsync();

            await trigger.ClickAsync();
            await Assertions.Expect(filter).ToBeVisibleAsync();
            await page.Locator(".resource-pane-search").ClickAsync();
            await Assertions.Expect(filter).ToBeHiddenAsync();
            await Assertions.Expect(page.Locator(".resource-row[data-resource-name='TestResource'] .resource-row-link")).ToHaveAttributeAsync("aria-current", "page");
            Assert.EndsWith("/resources/TestResource", page.Url, StringComparison.Ordinal);
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task Filter_NativeDismissHidesDialogBeforeServerRender()
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(1440, 900);
            await OpenResourcePaneAsync(page);
            await page.GetByRole(AriaRole.Button, new() { Name = Resources.ControlsStrings.ChartContainerFiltersHeader, Exact = true }).ClickAsync();
            var dialog = page.Locator(".resource-filter-popover").Locator("[part='dialog']");
            await Assertions.Expect(dialog).ToBeVisibleAsync();

            // Measure in the same browser task as native dismissal, before Blazor can remove the content.
            Assert.True(await dialog.EvaluateAsync<bool>("e => { e.hidePopover(); const bounds = e.getBoundingClientRect(); return bounds.width === 0 && bounds.height === 0; }"),
                "A natively dismissed dialog must disappear without waiting for the server to render.");
            await Assertions.Expect(dialog).ToBeHiddenAsync();
        });
    }

    [Theory]
    [InlineData(1440, 300)]
    [InlineData(800, 200)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task Filter_ShortViewportContainsActions(int width, int height)
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(width, height);
            await OpenResourcePaneAsync(page);
            await page.GetByRole(AriaRole.Button, new() { Name = Resources.ControlsStrings.ChartContainerFiltersHeader, Exact = true }).ClickAsync();
            var filter = page.GetByRole(AriaRole.Group, new() { Name = Resources.ControlsStrings.ChartContainerFiltersHeader, Exact = true });
            await Assertions.Expect(filter).ToBeVisibleAsync();
            await AssertContainedAsync(page, filter);
            await filter.GetByRole(AriaRole.Checkbox, new() { Name = KnownResourceTypes.Project, Exact = true }).UncheckAsync();
            await Assertions.Expect(page.Locator(".resource-pane .resource-row")).ToHaveCountAsync(0);
            await filter.GetByRole(AriaRole.Button, new() { Name = Resources.Layout.ResourcePaneFilterReset, Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".resource-pane .resource-row")).ToHaveCountAsync(1);
            await filter.GetByRole(AriaRole.Button, new() { Name = Resources.Layout.ResourcePaneFilterDone, Exact = true }).ClickAsync();
            await Assertions.Expect(filter).ToBeHiddenAsync();
        });
    }

    internal static async Task OpenResourcePaneAsync(IPage page, string resourceName = "TestResource")
    {
        await page.GotoAsync($"/resources/{resourceName}");
        var title = page.Locator("h1.resource-title");
        await Assertions.Expect(title).ToHaveTextAsync(resourceName);
        if (page.ViewportSize!.Width <= 768)
        {
            await title.GetByRole(AriaRole.Button, new() { Name = resourceName, Exact = true }).ClickAsync();
        }
    }

    internal static async Task AssertContainedAsync(IPage page, ILocator filter)
    {
        await AsyncTestHelpers.AssertIsTrueRetryAsync(async () =>
        {
            var viewport = page.ViewportSize!;
            foreach (var element in new[]
            {
                page.Locator(".resource-filter-popover").Locator("[part='dialog']"),
                filter,
                filter.GetByRole(AriaRole.Button, new() { Name = Resources.Layout.ResourcePaneFilterReset, Exact = true }),
                filter.GetByRole(AriaRole.Button, new() { Name = Resources.Layout.ResourcePaneFilterDone, Exact = true })
            })
            {
                var bounds = await element.BoundingBoxAsync();
                if (bounds is null || bounds.X < 0 || bounds.Y < 0 ||
                    bounds.X + bounds.Width > viewport.Width || bounds.Y + bounds.Height > viewport.Height)
                {
                    return false;
                }
            }

            return true;
        }, "The filter and both footer actions must fit inside the viewport, not just the document scroll width.");
    }

    public sealed class SingleResourceFixture : DashboardServerFixture
    {
        protected override IReadOnlyList<ResourceViewModel>? Resources => [MockDashboardClient.TestResource1];
    }
}

[RequiresFeature(TestFeature.Playwright)]
public sealed class ResourcePaneLongFilterTests : PlaywrightTestsBase<ResourcePaneLongFilterTests.ManyResourceTypesFixture>
{
    public ResourcePaneLongFilterTests(ManyResourceTypesFixture dashboardServerFixture)
        : base(dashboardServerFixture)
    {
    }

    [Theory]
    [InlineData(390, 844)]
    [InlineData(768, 1024)]
    [InlineData(1440, 900)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task Filter_LongContentScrollsWithActionsVisibleAndResizes(int width, int height)
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(width, height);
            await ResourcePaneFilterTests.OpenResourcePaneAsync(page, "resource-00");
            var trigger = page.GetByRole(AriaRole.Button, new() { Name = Resources.ControlsStrings.ChartContainerFiltersHeader, Exact = true });
            await trigger.ClickAsync();
            var filter = page.GetByRole(AriaRole.Group, new() { Name = Resources.ControlsStrings.ChartContainerFiltersHeader, Exact = true });
            await Assertions.Expect(filter).ToBeVisibleAsync();
            await ResourcePaneFilterTests.AssertContainedAsync(page, filter);

            var body = filter.Locator(".resource-filter-body");
            Assert.True(await body.EvaluateAsync<bool>("e => e.scrollHeight > e.clientHeight"));
            await body.HoverAsync();
            await page.Mouse.WheelAsync(0, 1500);
            await AsyncTestHelpers.AssertIsTrueRetryAsync(async () => await body.EvaluateAsync<bool>("e => e.scrollTop > 0"),
                "The long list should scroll independently of the footer.");
            var lastType = filter.GetByRole(AriaRole.Checkbox, new() { Name = "Type 23", Exact = true });
            await lastType.UncheckAsync();
            await Assertions.Expect(lastType).Not.ToBeCheckedAsync();
            await Assertions.Expect(page.Locator(".resource-pane .resource-row")).ToHaveCountAsync(23);
            await ResourcePaneFilterTests.AssertContainedAsync(page, filter);
            await filter.GetByRole(AriaRole.Button, new() { Name = Resources.Layout.ResourcePaneFilterReset, Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".resource-pane .resource-row")).ToHaveCountAsync(24);
            await Assertions.Expect(filter.GetByRole(AriaRole.Button, new() { Name = Resources.Layout.ResourcePaneFilterReset, Exact = true })).ToBeDisabledAsync();

            foreach (var viewport in new[] { (Width: 1440, Height: 900), (Width: 768, Height: 1024), (Width: 390, Height: 844) })
            {
                await page.SetViewportSizeAsync(viewport.Width, viewport.Height);
                // The narrow header keeps its tabs while the drawer is open, but removes its title button.
                await Assertions.Expect(page.Locator(".resource-context-header > .resource-tabs")).ToHaveCountAsync(viewport.Width <= 768 ? 1 : 0);
                if (viewport.Width <= 768 && !await page.Locator(".resource-pane").IsVisibleAsync())
                {
                    await page.Locator("h1.resource-title").GetByRole(AriaRole.Button, new() { Name = "resource-00", Exact = true }).ClickAsync();
                }
                await Assertions.Expect(trigger).ToBeVisibleAsync();
                if (!await filter.IsVisibleAsync())
                {
                    await trigger.ClickAsync();
                }
                await ResourcePaneFilterTests.AssertContainedAsync(page, filter);
            }

            await filter.GetByRole(AriaRole.Button, new() { Name = Resources.Layout.ResourcePaneFilterDone, Exact = true }).ClickAsync();
            await Assertions.Expect(filter).ToBeHiddenAsync();
            await Assertions.Expect(trigger).ToHaveAttributeAsync("aria-expanded", "false");
            await Assertions.Expect(page.Locator(".resource-row[data-resource-name='resource-00'] .resource-row-link")).ToHaveAttributeAsync("aria-current", "page");
            Assert.EndsWith("/resources/resource-00", page.Url, StringComparison.Ordinal);
        });
    }

    public sealed class ManyResourceTypesFixture : DashboardServerFixture
    {
        protected override IReadOnlyList<ResourceViewModel>? Resources => Enumerable.Range(0, 24)
            .Select(i => ModelTestHelpers.CreateResource(resourceName: $"resource-{i:00}", resourceType: $"Type {i:00}", state: KnownResourceState.Running))
            .ToList();
    }
}
