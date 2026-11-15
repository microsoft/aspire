// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;
using Aspire.TestUtilities;
using Microsoft.Playwright;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration.Playwright;

[RequiresFeature(TestFeature.Playwright)]
public sealed class ResourcePaneContextMenuTests(ResourceTagPaneTests.TaggedResourcesFixture fixture)
    : PlaywrightTestsBase<ResourceTagPaneTests.TaggedResourcesFixture>(fixture)
{
    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ContextMenu_AfterEllipsisSwitchesToIntendedRow()
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(1440, 900);
            await page.GotoAsync("/resources?resource=tag-a&resource=tag-c");
            var url = page.Url;
            var ellipsis = page.Locator("[data-resource-name='tag-a']")
                .GetByRole(AriaRole.Button, new() { Name = "Actions for tag-a", Exact = true });
            var target = page.Locator("[data-resource-name='tag-b'] .resource-row-link");
            var menu = page.Locator("fluent-menu[aria-labelledby='resource-pane-context-menu-header'] fluent-menu-list");
            var selected = page.Locator(".resource-row-link[aria-current='true']");

            foreach (var keyboard in new[] { false, true })
            {
                await ellipsis.ClickAsync();
                await Assertions.Expect(page.GetByRole(AriaRole.Menuitem, new() { Name = "View JSON", Exact = true })).ToBeVisibleAsync();
                if (keyboard)
                {
                    await target.PressAsync("Shift+F10");
                }
                else
                {
                    await target.ClickAsync(new() { Button = MouseButton.Right });
                }

                await Assertions.Expect(menu).ToBeVisibleAsync();
                await Assertions.Expect(menu.GetByText("tag-b", new() { Exact = true })).ToBeVisibleAsync();
                await Assertions.Expect(ellipsis).ToHaveAttributeAsync("aria-expanded", "false");
                await Assertions.Expect(page.Locator(".aspire-menu-container fluent-menu-list:popover-open")).ToHaveCountAsync(1);
                await Assertions.Expect(selected).ToHaveCountAsync(2);
                Assert.Equal(url, page.Url);
                await page.Keyboard.PressAsync("Escape");
                await Assertions.Expect(target).ToBeFocusedAsync();
            }

            await target.ClickAsync(new() { Button = MouseButton.Right });
            await Assertions.Expect(menu).ToBeVisibleAsync();
            var other = page.Locator("[data-resource-name='tag-c'] .resource-row-link");
            await other.ClickAsync(new() { Button = MouseButton.Right, Position = new() { X = 8, Y = 8 } });
            await Assertions.Expect(menu.GetByText("tag-c", new() { Exact = true })).ToBeVisibleAsync();
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(other).ToBeFocusedAsync();
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ContextMenu_TargetsRowAndRestoresKeyboardFocusWithoutChangingSelection()
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(1440, 900);
            await page.GotoAsync("/resources?resource=tag-a&resource=tag-c");
            var target = page.Locator("[data-resource-name='tag-b'] .resource-row-link");
            await Assertions.Expect(target).ToBeVisibleAsync();
            var url = page.Url;
            var menu = page.Locator("fluent-menu[aria-labelledby='resource-pane-context-menu-header'] fluent-menu-list");
            var selected = page.Locator(".resource-row-link[aria-current='true']");
            await Assertions.Expect(selected).ToHaveCountAsync(2);

            await target.ClickAsync(new() { Button = MouseButton.Right });
            await Assertions.Expect(menu).ToBeVisibleAsync();
            await Assertions.Expect(menu.GetByText("tag-b", new() { Exact = true })).ToBeVisibleAsync();
            Assert.Equal(url, page.Url);
            await Assertions.Expect(selected).ToHaveCountAsync(2);
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(menu).Not.ToBeVisibleAsync();
            await Assertions.Expect(target).ToBeFocusedAsync();

            foreach (var key in new[] { "Shift+F10", "ContextMenu" })
            {
                await target.PressAsync(key);
                await Assertions.Expect(menu).ToBeVisibleAsync();
                await Assertions.Expect(menu.GetByRole(AriaRole.Menuitem, new() { Name = "View JSON", Exact = true })).ToBeFocusedAsync();
                await page.Keyboard.PressAsync("Escape");
                await Assertions.Expect(target).ToBeFocusedAsync();
            }

            await target.PressAsync("Shift+F10");
            await menu.GetByRole(AriaRole.Menuitem, new() { Name = "View JSON", Exact = true }).ClickAsync();
            var dialog = page.GetByRole(AriaRole.Dialog);
            await Assertions.Expect(dialog).ToBeVisibleAsync();
            await Assertions.Expect(dialog.Locator(":focus")).ToHaveCountAsync(1);
            Assert.Equal(url, page.Url);
        });
    }

    [Theory]
    [InlineData(390, 844)]
    [InlineData(768, 1024)]
    [InlineData(1440, 900)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ContextMenu_FitsViewportAndLeavesVisibleFallback(int width, int height)
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(width, height);
            await page.GotoAsync("/resources/tag-a");
            if (width <= 768)
            {
                await page.Locator(".resource-picker").ClickAsync();
            }
            var row = page.Locator("[data-resource-name='tag-b']");
            await Assertions.Expect(row).ToBeVisibleAsync();
            var fallback = row.GetByRole(AriaRole.Button, new() { Name = "Actions for tag-b", Exact = true });
            await Assertions.Expect(fallback).ToBeVisibleAsync();
            var link = row.Locator(".resource-row-link");
            var box = await link.BoundingBoxAsync();
            Assert.NotNull(box);
            await link.ClickAsync(new() { Button = MouseButton.Right, Position = new() { X = box.Width - 8, Y = box.Height / 2 } });
            var menu = page.Locator("fluent-menu[aria-labelledby='resource-pane-context-menu-header'] fluent-menu-list");
            await Assertions.Expect(menu).ToBeVisibleAsync();
            var bounds = await menu.BoundingBoxAsync();
            Assert.NotNull(bounds);
            Assert.InRange(bounds.X, 0, width - bounds.Width);
            Assert.InRange(bounds.Y, 0, height - bounds.Height);
            await page.Keyboard.PressAsync("Escape");
            await fallback.ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Menuitem, new() { Name = "View JSON", Exact = true })).ToBeVisibleAsync();
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ContextMenu_NearBottomEdgeRemainsInsideViewport()
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(390, 540);
            await page.GotoAsync("/resources/tag-a");
            await page.Locator(".resource-picker").ClickAsync();
            var link = page.Locator("[data-resource-name='tag-c'] .resource-row-link");
            await link.ScrollIntoViewIfNeededAsync();
            var box = await link.BoundingBoxAsync();
            Assert.NotNull(box);
            await link.ClickAsync(new() { Button = MouseButton.Right, Position = new() { X = box.Width - 8, Y = box.Height - 3 } });
            var menu = page.Locator("fluent-menu[aria-labelledby='resource-pane-context-menu-header'] fluent-menu-list");
            await Assertions.Expect(menu).ToBeVisibleAsync();
            var bounds = await menu.BoundingBoxAsync();
            Assert.NotNull(bounds);
            Assert.True(box.Y + box.Height - 3 + bounds.Height > 540);
            Assert.InRange(bounds.X, 0, 390 - bounds.Width);
            Assert.InRange(bounds.Y, 0, 540 - bounds.Height);
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ContextMenu_RepeatedTagRowsRestoreExactTriggerAndRespectLightDismissal()
    {
        await RunTestAsync(async page =>
        {
            await DashboardServerFixture.AddResourceTagsAsync(page, new() { ["tag-b"] = ["Backend", "Public"] });
            await page.SetViewportSizeAsync(1440, 900);
            await page.GotoAsync("/resources/tag-a?pane=tags");
            var rows = page.Locator("[data-resource-name='tag-b'] .resource-row-link");
            await Assertions.Expect(rows).ToHaveCountAsync(2);
            var target = rows.Last;
            var menu = page.Locator("fluent-menu[aria-labelledby='resource-pane-context-menu-header'] fluent-menu-list");
            await target.PressAsync("Shift+F10");
            await Assertions.Expect(menu).ToBeVisibleAsync();
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(target).ToBeFocusedAsync();

            await target.PressAsync("Shift+F10");
            await Assertions.Expect(menu).ToBeVisibleAsync();
            var search = page.Locator(".resource-pane").GetByRole(AriaRole.Searchbox);
            await search.ClickAsync();
            await Assertions.Expect(menu).Not.ToBeVisibleAsync();
            await Assertions.Expect(search).ToBeFocusedAsync();
        });
    }
}
