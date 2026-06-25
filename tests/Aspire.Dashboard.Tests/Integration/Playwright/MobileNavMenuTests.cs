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

        var settingsItem = page.Locator($"fluent-menu.mobile-nav-menu fluent-menu-item[title='{SettingsMenuItemTitle}']");
        await settingsItem.FocusAsync();

        var metrics = await settingsItem.EvaluateAsync<MobileNavFocusMetrics>("""
            element => {
                const menu = document.querySelector('fluent-menu.mobile-nav-menu');
                const menuRect = menu.getBoundingClientRect();
                const focusedRect = element.getBoundingClientRect();
                const style = getComputedStyle(menu);
                return {
                    activeTitle: element.getAttribute('title'),
                    menuTop: menuRect.top,
                    menuBottom: menuRect.bottom,
                    focusedTop: focusedRect.top,
                    focusedBottom: focusedRect.bottom,
                    viewportHeight: innerHeight,
                    paddingTop: style.paddingTop,
                    paddingBottom: style.paddingBottom,
                    scrollPaddingTop: style.scrollPaddingTop,
                    scrollPaddingBottom: style.scrollPaddingBottom
                };
            }
            """);

        Assert.Equal(SettingsMenuItemTitle, metrics.ActiveTitle);
        Assert.True(metrics.MenuTop >= 0, $"Menu starts above viewport: {metrics}");
        Assert.True(metrics.MenuBottom <= metrics.ViewportHeight, $"Menu extends below viewport: {metrics}");
        Assert.True(metrics.FocusedTop >= metrics.MenuTop, $"Focused item starts above menu scrollport: {metrics}");
        Assert.True(metrics.FocusedBottom <= metrics.MenuBottom, $"Focused item extends below menu scrollport: {metrics}");
        Assert.Equal("4px", metrics.PaddingTop);
        Assert.Equal("4px", metrics.PaddingBottom);
        Assert.Equal("4px", metrics.ScrollPaddingTop);
        Assert.Equal("4px", metrics.ScrollPaddingBottom);
    }

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

        public string ScrollPaddingTop { get; set; } = null!;

        public string ScrollPaddingBottom { get; set; } = null!;
    }
}
