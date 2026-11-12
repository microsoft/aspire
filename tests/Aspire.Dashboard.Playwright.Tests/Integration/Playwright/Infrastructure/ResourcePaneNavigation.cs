// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Playwright;

namespace Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;

internal static class ResourcePaneNavigation
{
    /// <summary>
    /// Opens the overview of all resources from the dashboard's start page, /resources, which opens (and reloads) on the
    /// full-page resource list on desktop. Summary opens the overview. Narrow layouts have no full-page list and
    /// already show the overview.
    /// </summary>
    public static async Task OpenAllResourcesOverviewAsync(IPage page)
    {
        await Assertions.Expect(page.Locator(".resources-layout")).ToHaveAttributeAsync("data-pane-interactions-ready", "true");
        if (await page.Locator(".resources-layout.pane-full-canvas").CountAsync() > 0)
        {
            await page.Locator(".resource-pane-view-selector").GetByRole(AriaRole.Button, new() { Name = Resources.Dialogs.SettingsDialogGroupOverviewSummary, Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".resources-layout.pane-full-canvas")).ToHaveCountAsync(0);
        }
    }

    public static async Task ToggleResourceListAsync(IPage page)
    {
        await Assertions.Expect(page.Locator(".resources-layout")).ToHaveAttributeAsync("data-pane-interactions-ready", "true");
        var isFullCanvas = await page.Locator(".pane-full-canvas").CountAsync() > 0;
        var table = page.Locator(".resource-header-actions").GetByRole(AriaRole.Button, new() { Name = Resources.ControlsStrings.ChartContainerTableTab, Exact = true });
        if (isFullCanvas)
        {
            await page.Locator(".resource-pane-view-selector").GetByRole(AriaRole.Button, new() { Name = Resources.Dialogs.SettingsDialogGroupOverviewSummary, Exact = true }).ClickAsync();
        }
        else
        {
            if (!await table.IsVisibleAsync())
            {
                var allResources = page.Locator(".resource-row-all .resource-row-link");
                await allResources.ClickAsync();
                await Assertions.Expect(allResources).ToHaveAttributeAsync("aria-current", "page");
            }
            if (await page.Locator(".pane-full-canvas").CountAsync() == 0)
            {
                await table.ClickAsync();
            }
        }
        await Assertions.Expect(page.Locator(".pane-full-canvas")).ToHaveCountAsync(isFullCanvas ? 0 : 1);
        // All resources can navigate before Table expands the picker. Wait for the router and any pane animation.
        await page.WaitForFunctionAsync("""
            () => {
                const layout = document.querySelector('.resources-layout');
                return layout.dataset.paneLocation === location.href &&
                    !layout.getAnimations().some(animation => animation.playState === 'running');
            }
            """);
    }
}
