// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Utils;
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

    /// <summary>
    /// Runs a test in a new page.
    /// </summary>
    /// <param name="test">The test.</param>
    /// <param name="useDefaultDensity">
    /// Whether the resource list starts in the dashboard's default density, compact. Otherwise it starts comfortable:
    /// most tests were written for its two-line rows, with their state text, availability badges and row actions shown
    /// at rest, and set another density when they test it. Pass <see langword="true"/> to test the defaults.
    /// </param>
    public async Task RunTestAsync(Func<IPage, Task> test, bool useDefaultDensity = false)
    {
        var page = await CreateNewPageAsync();
        if (!useDefaultDensity)
        {
            // Only when nothing is stored, so a density the test chooses in the settings survives a reload. Stored
            // values are JSON, so the density is a JSON string.
            await page.AddInitScriptAsync($$"""
                if (localStorage.getItem('{{BrowserStorageKeys.ResourceListDensity}}') === null) {
                    localStorage.setItem('{{BrowserStorageKeys.ResourceListDensity}}', '"{{nameof(ResourceListDensity.Comfortable)}}"');
                }
                """);
        }

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
