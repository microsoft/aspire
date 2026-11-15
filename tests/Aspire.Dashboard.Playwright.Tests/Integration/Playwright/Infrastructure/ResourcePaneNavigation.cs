// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;

internal static class ResourcePaneNavigation
{
    /// <summary>
    /// Matches the dashboard's home URL, "/", where the resource list fills the page as a table.
    /// </summary>
    public static readonly Regex HomeUrl = new(@"^https?://[^/]+/$");

    /// <summary>
    /// The brand in the header, which is the home button on desktop.
    /// </summary>
    public static ILocator HomeLink(IPage page) => page.Locator("header .brand-home a.application-name");

    /// <summary>
    /// What switches between a page with the list docked beside it and the home table: the brand while the list is
    /// docked, and the table's "All resources" row, which docks it again, at home. Exactly one of them matches.
    /// </summary>
    public static ILocator TableToggle(IPage page) =>
        page.Locator(".layout:not(:has(.pane-full-canvas)) header .brand-home a.application-name, .pane-full-canvas .resource-row-all .resource-row-link");

    /// <summary>
    /// Goes to the home table with the brand, as a user would on desktop.
    /// </summary>
    public static async Task GoHomeAsync(IPage page)
    {
        await HomeLink(page).ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(HomeUrl);
        await Assertions.Expect(page.Locator(".resources-layout.pane-full-canvas")).ToBeVisibleAsync();
    }

    /// <summary>
    /// Opens the overview of all resources. The dashboard's home page, "/", is the full-page resource list with no view
    /// open on desktop, and its "All resources" row opens the overview at /resources. Narrow layouts have no full-page
    /// list and already show the overview, as does /resources itself.
    /// </summary>
    public static async Task OpenAllResourcesOverviewAsync(IPage page)
    {
        await Assertions.Expect(page.Locator(".resources-layout")).ToHaveAttributeAsync("data-pane-interactions-ready", "true");
        if (await page.Locator(".resources-layout.pane-full-canvas").CountAsync() > 0)
        {
            await page.Locator(".resource-row-all .resource-row-link").ClickAsync();
            await Assertions.Expect(page.Locator(".resources-layout.pane-full-canvas")).ToHaveCountAsync(0);
        }
    }

    /// <summary>
    /// Switches between a page with the resource list docked beside it and the full-page table. The table is the home
    /// page, "/", opened from the brand in the header. Its "All resources" row opens the overview of all resources,
    /// /resources, with the list docked.
    /// </summary>
    public static async Task ToggleResourceListAsync(IPage page)
    {
        await Assertions.Expect(page.Locator(".resources-layout")).ToHaveAttributeAsync("data-pane-interactions-ready", "true");
        var isFullCanvas = await page.Locator(".pane-full-canvas").CountAsync() > 0;
        var origin = new Uri(page.Url).GetLeftPart(UriPartial.Authority);
        await TableToggle(page).ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(origin + (isFullCanvas ? "/resources" : "/"));
        await Assertions.Expect(page.Locator(".pane-full-canvas")).ToHaveCountAsync(isFullCanvas ? 0 : 1);
        // The button navigates as well as changing the presentation. Wait for both the router
        // and browser URL before testing the result.
        await page.WaitForFunctionAsync("""
            () => {
                const layout = document.querySelector('.resources-layout');
                return layout.dataset.paneLocation === location.href &&
                    !layout.getAnimations().some(animation => animation.playState === 'running');
            }
            """);
    }
}
