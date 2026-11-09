// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Playwright;

namespace Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;

internal static class ResourcePaneNavigation
{
    /// <summary>
    /// Opens the overview of all resources from the dashboard's start page, /resources, which opens (and reloads) on the
    /// full-page resource list with no view open on desktop. "All resources" opens the current tab's view, which is the
    /// overview. Narrow layouts have no full-page list and already show the overview.
    /// </summary>
    public static async Task OpenAllResourcesOverviewAsync(IPage page)
    {
        await Assertions.Expect(page.Locator(".resources-layout")).ToHaveAttributeAsync("data-pane-interactions-ready", "true");
        if (await page.Locator(".resources-layout.pane-full-canvas").CountAsync() > 0)
        {
            await page.Locator(".resource-select-all-toggle").ClickAsync();
            await Assertions.Expect(page.Locator(".resources-layout.pane-full-canvas")).ToHaveCountAsync(0);
        }
    }

    public static async Task ToggleResourceListAsync(IPage page)
    {
        await Assertions.Expect(page.Locator(".resources-layout")).ToHaveAttributeAsync("data-pane-interactions-ready", "true");
        var isFullCanvas = await page.Locator(".pane-full-canvas").CountAsync() > 0;
        var toggle = page.Locator(isFullCanvas ? ".pane-full-canvas .resource-select-all-toggle" : ".detail-page-close");
        var destination = isFullCanvas ? null : new Uri(new Uri(page.Url),
            await toggle.GetAttributeAsync("data-parent-url") ?? throw new InvalidOperationException("Close view must expose its parent URL.")).AbsoluteUri;
        await toggle.ClickAsync();
        if (destination is not null)
        {
            await Assertions.Expect(page).ToHaveURLAsync(destination);
        }
        else
        {
            await Assertions.Expect(page.Locator(".resource-select-all-toggle")).ToHaveAttributeAsync("aria-description", Resources.Layout.ResourcePaneAllSelected);
        }
        await Assertions.Expect(page.Locator(".pane-full-canvas")).ToHaveCountAsync(isFullCanvas ? 0 : 1);
        // Close view navigates as well as hiding content. Wait for both the router
        // and browser URL before testing the resulting presentation.
        await page.WaitForFunctionAsync("""
            () => {
                const layout = document.querySelector('.resources-layout');
                return layout.dataset.paneLocation === location.href &&
                    !layout.getAnimations().some(animation => animation.playState === 'running');
            }
            """);
    }
}
