// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Aspire.Dashboard.Resources;
using Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;
using Aspire.Dashboard.Utils;
using Aspire.TestUtilities;
using Microsoft.Playwright;
using Xunit;
using ResourceStrings = Aspire.Dashboard.Resources.Resources;

namespace Aspire.Dashboard.Tests.Integration.Playwright;

[RequiresFeature(TestFeature.Playwright)]
public class OverviewLayoutTests(ResourcesTests.ResourcesDashboardServerFixture fixture)
    : PlaywrightTestsBase<ResourcesTests.ResourcesDashboardServerFixture>(fixture)
{
    [Theory]
    [InlineData(390, 844, "Light")]
    [InlineData(390, 844, "Dark")]
    [InlineData(768, 1024, "Light")]
    [InlineData(768, 1024, "Dark")]
    [InlineData(1440, 900, "Light")]
    [InlineData(1440, 900, "Dark")]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task RemovedCards_UsesSummaryAndPreservesScopeDetailsAndKeyboard(int width, int height, string theme)
    {
        await RunTestAsync(async page =>
        {
            var errors = new ConcurrentQueue<string>();
            page.PageError += (_, error) => errors.Enqueue(error);
            page.Console += (_, message) =>
            {
                if (message.Type == "error")
                {
                    errors.Enqueue(message.Text);
                }
            };
            await page.SetViewportSizeAsync(width, height);
            await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
            await page.Context.AddCookiesAsync([new Cookie { Name = "currentTheme", Value = theme, Url = DashboardServerFixture.DashboardApp.FrontendSingleEndPointAccessor().GetResolvedAddress() }]);
            await page.GotoAsync("/resources");
            await ResourcePaneNavigation.OpenAllResourcesOverviewAsync(page);
            await Assertions.Expect(page.Locator(".home-hero")).ToBeVisibleAsync();
            await page.EvaluateAsync("key => localStorage.setItem(key, JSON.stringify('Cards'))", BrowserStorageKeys.GroupOverview);
            await page.ReloadAsync();
            await ResourcePaneNavigation.OpenAllResourcesOverviewAsync(page);
            await Assertions.Expect(page.Locator(".home-hero")).ToBeVisibleAsync();
            await page.WaitForFunctionAsync("key => JSON.parse(localStorage.getItem(key)) === 'Summary'", BrowserStorageKeys.GroupOverview);
            await Assertions.Expect(page.Locator(".home-hero-headline")).ToHaveTextAsync("All 3 resources are running");
            var layout = page.GetByRole(AriaRole.Group, new() { Name = ResourceStrings.LayoutLabel, Exact = true });
            await Assertions.Expect(layout.GetByRole(AriaRole.Button)).ToHaveCountAsync(2);
            var summary = layout.GetByRole(AriaRole.Button, new() { Name = Dialogs.SettingsDialogGroupOverviewSummary, Exact = true });
            var graph = layout.GetByRole(AriaRole.Button, new() { Name = ResourceStrings.GraphPageTitle, Exact = true });
            await Assertions.Expect(summary).ToHaveAttributeAsync("aria-pressed", "true");
            await Assertions.Expect(page.Locator(".resource-card-grid, .home-resource-table")).ToHaveCountAsync(0);
            await AssertFitsAsync(layout);

            await graph.FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await Assertions.Expect(page.Locator(".home-resource-graph-layout")).ToBeVisibleAsync();
            await Assertions.Expect(graph).ToHaveAttributeAsync("aria-pressed", "true");
            await summary.FocusAsync();
            await page.Keyboard.PressAsync("Space");
            await Assertions.Expect(page.Locator(".home-hero")).ToBeVisibleAsync();
            await Assertions.Expect(summary).ToBeFocusedAsync();

            await page.GotoAsync("/graph");
            await Assertions.Expect(page.Locator(".home-resource-graph-layout")).ToBeVisibleAsync();
            await summary.ClickAsync();
            await Assertions.Expect(page.Locator(".home-hero")).ToBeVisibleAsync();

            await page.GotoAsync("/resources?resource=apigateway&resource=TestResource");
            await Assertions.Expect(page.Locator(".home-hero-headline")).ToHaveTextAsync("All 2 resources are running");
            await Assertions.Expect(page.Locator(".home-activity")).ToBeVisibleAsync();
            // The fixture has no request telemetry, so the insight panels stay hidden.
            await Assertions.Expect(page.Locator(".home-insights")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator(".home-problems")).ToHaveCountAsync(1);
            await page.GotoAsync("/resources/TestResource");
            await Assertions.Expect(page.Locator(".overview-grid")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".home-hero")).ToHaveCountAsync(0);
            await page.GoBackAsync();
            await Assertions.Expect(page.Locator(".home-hero-headline")).ToHaveTextAsync("All 2 resources are running");

            await page.GotoAsync("/resources");
            await ResourcePaneNavigation.OpenAllResourcesOverviewAsync(page);
            await Assertions.Expect(page.Locator(".home-hero-headline")).ToHaveTextAsync("All 3 resources are running");
            if (width <= 768)
            {
                await page.Locator(".resource-picker").ClickAsync();
            }
            await page.Locator("#resourcePaneGroupingButton").ClickAsync();
            await page.GetByRole(AriaRole.Menuitemradio, new() { Name = Layout.ResourcePaneGroupingResourceType, Exact = true }).ClickAsync();
            var group = page.Locator(".resource-tag-group").Filter(new() { Has = page.GetByText("Project", new() { Exact = true }) });
            await group.Locator(".resource-tag-link").ClickAsync();
            await Assertions.Expect(page.Locator(".home-hero-headline")).ToHaveTextAsync("All 2 resources are running");

            await OpenSettingsAsync(page);
            var options = page.GetByRole(AriaRole.Group, new() { Name = Dialogs.SettingsDialogGroupOverview, Exact = true });
            await Assertions.Expect(options.GetByRole(AriaRole.Button)).ToHaveCountAsync(2);
            await Assertions.Expect(options.GetByRole(AriaRole.Button, new() { Name = Dialogs.SettingsDialogGroupOverviewSummary, Exact = true })).ToHaveAttributeAsync("aria-pressed", "true");
            await AssertFitsAsync(options);
            await page.GetByRole(AriaRole.Button, new() { Name = Dialogs.DialogCloseButtonText, Exact = true }).Last.ClickAsync();
            Assert.Empty(errors);
        });
    }

    private static async Task AssertFitsAsync(ILocator control)
    {
        var fits = await control.EvaluateAsync<bool>("el => { const r = el.getBoundingClientRect(); return r.left >= 0 && r.right <= innerWidth && el.scrollWidth <= el.clientWidth + 1; }");
        Assert.True(fits);
    }

    private static async Task OpenSettingsAsync(IPage page)
    {
        var settings = page.GetByRole(AriaRole.Button, new() { Name = Layout.MainLayoutLaunchSettings, Exact = true });
        if (await settings.IsVisibleAsync())
        {
            await settings.ClickAsync();
        }
        else
        {
            await page.Locator(".navigation-button").ClickAsync();
            await page.GetByRole(AriaRole.Menuitem, new() { Name = Layout.MainLayoutLaunchSettings, Exact = true }).ClickAsync();
        }
        await Assertions.Expect(page.GetByRole(AriaRole.Dialog)).ToBeVisibleAsync();
    }
}
