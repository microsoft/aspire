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
