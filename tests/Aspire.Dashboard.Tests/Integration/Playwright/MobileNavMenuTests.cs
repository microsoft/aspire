// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;
using Aspire.TestUtilities;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Playwright;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration.Playwright;

[RequiresFeature(TestFeature.Playwright)]
public sealed class MobileNavMenuTests : PlaywrightTestsBase<DashboardServerFixture>
{
    private const string SettingsMenuItemTitle = "Settings";

    public MobileNavMenuTests(DashboardServerFixture dashboardServerFixture)
        : base(dashboardServerFixture)
    {
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task MobileNavFocusRemainsVisibleAtHighZoomViewport()
    {
        await using var context = await PlaywrightFixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            BaseURL = DashboardServerFixture.DashboardApp.FrontendSingleEndPointAccessor().GetResolvedAddress(),
            ViewportSize = new ViewportSize { Width = 640, Height = 384 }
        });
        var page = await context.NewPageAsync();

        await page.GotoAsync("/").DefaultTimeout();
        await Assertions.Expect(page.GetByText(MockDashboardClient.TestResource1.DisplayName)).ToBeVisibleAsync();

        await page.Locator(".navigation-button").ClickAsync();
        var menu = page.Locator("fluent-menu.mobile-nav-menu");
        await Assertions.Expect(menu).ToBeVisibleAsync();

        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(menu).ToBeHiddenAsync();
        var activeElementId = await page.EvaluateAsync<string?>("() => document.activeElement?.id");
        Assert.Equal("dashboard-navigation-button", activeElementId);

        await page.Locator(".navigation-button").ClickAsync();
        await Assertions.Expect(menu).ToBeVisibleAsync();

        await page.Keyboard.PressAsync("Tab");

        var focusHistory = new List<string?>();
        for (var i = 0; i < 15; i++)
        {
            var activeTitle = await page.EvaluateAsync<string?>(GetActiveMenuItemTitleScript);
            focusHistory.Add(activeTitle ?? await page.EvaluateAsync<string>("""
                () => {
                    const element = document.activeElement;
                    return `${element?.tagName ?? '<null>'}:${element?.className ?? ''}:${element?.getAttribute?.('title') ?? ''}`;
                }
                """));
            if (activeTitle == SettingsMenuItemTitle)
            {
                break;
            }

            await page.Keyboard.PressAsync("ArrowDown");
        }

        var metrics = await page.EvaluateAsync<MobileNavFocusMetrics>("""
            () => {
                const menu = document.querySelector('fluent-menu.mobile-nav-menu');
                const focusedMenuItem = getActiveMenuItem();
                const menuRect = menu.getBoundingClientRect();
                const focusedRect = focusedMenuItem?.getBoundingClientRect() ?? new DOMRect();
                const style = getComputedStyle(menu);
                return {
                    activeTitle: focusedMenuItem?.getAttribute('title'),
                    menuTop: menuRect.top,
                    menuBottom: menuRect.bottom,
                    focusedTop: focusedRect.top,
                    focusedBottom: focusedRect.bottom,
                    viewportHeight: innerHeight,
                    paddingTop: style.paddingTop,
                    paddingBottom: style.paddingBottom,
                    paddingTopValue: Number.parseFloat(style.paddingTop),
                    paddingBottomValue: Number.parseFloat(style.paddingBottom),
                    scrollPaddingTop: style.scrollPaddingTop,
                    scrollPaddingBottom: style.scrollPaddingBottom
                };

                function getActiveMenuItem() {
                    const visited = new Set();
                    let element = document.activeElement;
                    while (element && !visited.has(element)) {
                        visited.add(element);
                        if (element.matches?.('fluent-menu-item')) {
                            return element;
                        }

                        if (element.shadowRoot?.activeElement) {
                            element = element.shadowRoot.activeElement;
                            continue;
                        }

                        element = element.getRootNode?.().host ?? null;
                    }

                    return null;
                }
            }
            """);

        Assert.True(metrics.ActiveTitle == SettingsMenuItemTitle, $"Focus did not reach the Settings menu item. History: {string.Join(" -> ", focusHistory)}");
        Assert.True(metrics.MenuTop >= 0, $"Menu starts above viewport: {metrics}");
        Assert.True(metrics.MenuBottom <= metrics.ViewportHeight, $"Menu extends below viewport: {metrics}");
        Assert.True(metrics.FocusedTop >= metrics.MenuTop + metrics.PaddingTopValue - 0.5, $"Focused item starts inside the menu focus padding: {metrics}");
        Assert.True(metrics.FocusedBottom <= metrics.MenuBottom - metrics.PaddingBottomValue + 0.5, $"Focused item ends inside the menu focus padding: {metrics}");
        Assert.Equal("4px", metrics.PaddingTop);
        Assert.Equal("4px", metrics.PaddingBottom);
        Assert.Equal("4px", metrics.ScrollPaddingTop);
        Assert.Equal("4px", metrics.ScrollPaddingBottom);

        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(menu).ToBeHiddenAsync();

        activeElementId = await page.EvaluateAsync<string?>("() => document.activeElement?.id");
        Assert.Equal("dashboard-navigation-button", activeElementId);
    }

    private const string GetActiveMenuItemTitleScript = """
        () => {
            const visited = new Set();
            let element = document.activeElement;
            while (element && !visited.has(element)) {
                visited.add(element);
                if (element.matches?.('fluent-menu-item')) {
                    return element.getAttribute('title');
                }

                if (element.shadowRoot?.activeElement) {
                    element = element.shadowRoot.activeElement;
                    continue;
                }

                element = element.getRootNode?.().host ?? null;
            }

            return null;
        }
        """;

    private sealed class MobileNavFocusMetrics
    {
        public string? ActiveTitle { get; set; }

        public double MenuTop { get; set; }

        public double MenuBottom { get; set; }

        public double FocusedTop { get; set; }

        public double FocusedBottom { get; set; }

        public double ViewportHeight { get; set; }

        public string PaddingTop { get; set; } = null!;

        public string PaddingBottom { get; set; } = null!;

        public double PaddingTopValue { get; set; }

        public double PaddingBottomValue { get; set; }

        public string ScrollPaddingTop { get; set; } = null!;

        public string ScrollPaddingBottom { get; set; } = null!;
    }
}
