// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Playwright;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;

public class PlaywrightTestsBase<TDashboardServerFixture> : IClassFixture<TDashboardServerFixture>, IAsyncLifetime
    where TDashboardServerFixture : DashboardServerFixture
{
    public DashboardServerFixture DashboardServerFixture { get; }
    public PlaywrightFixture PlaywrightFixture { get; }

    private IBrowserContext? _context;
    private bool _ownsTestGate;

    public PlaywrightTestsBase(DashboardServerFixture dashboardServerFixture)
    {
        DashboardServerFixture = dashboardServerFixture;
        PlaywrightFixture = dashboardServerFixture.PlaywrightFixture;
    }

    public async ValueTask InitializeAsync()
    {
        await DashboardServerFixture.TestGate.WaitAsync();
        _ownsTestGate = true;
    }

    public async Task RunTestAsync(Func<IPage, Task> test)
    {
        var page = await CreateNewPageAsync();
        try
        {
            await test(page);
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    /// <summary>
    /// Clicks a resource tab and waits until the server has rendered the tab strip for the new location.
    /// </summary>
    /// <remarks>
    /// A tab click changes the URL in the browser before the server renders the destination. Until that render
    /// reaches the browser, the tab strip on the page is the one rendered for the previous location, and
    /// ResourcesLayout deliberately ignores changes from it. Clicking another tab in that window is dropped.
    /// The strip is keyed by location, so the old element is removed from the DOM once the new strip is rendered.
    /// </remarks>
    public static async Task ClickResourceTabAndWaitForRenderAsync(IPage page, Func<Task> clickTab)
    {
        var tabStrip = await page.Locator(".resources-tab-header").ElementHandleAsync();
        await clickTab();
        await page.WaitForFunctionAsync("tabStrip => !tabStrip.isConnected", tabStrip);
    }

    private async Task<IPage> CreateNewPageAsync()
    {
        if (_context is null)
        {
            _context = await PlaywrightFixture.CreateContextAsync(new BrowserNewContextOptions
            {
                IgnoreHTTPSErrors = true,
                BaseURL = DashboardServerFixture.DashboardApp.FrontendSingleEndPointAccessor().GetResolvedAddress()
            });
        }

        return await _context.NewPageAsync();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_context is not null)
            {
                await _context.DisposeAsync();
            }
        }
        finally
        {
            if (_ownsTestGate)
            {
                _ownsTestGate = false;
                DashboardServerFixture.TestGate.Release();
            }
        }
    }
}
