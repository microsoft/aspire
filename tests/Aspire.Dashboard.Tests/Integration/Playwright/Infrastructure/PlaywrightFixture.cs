// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Templates.Tests;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Playwright;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;

public class PlaywrightFixture : IAsyncLifetime
{
    public IBrowser Browser { get; set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Assertions.SetDefaultExpectTimeout(TestConstants.DefaultTimeoutDuration);

        PlaywrightProvider.DetectAndSetInstalledPlaywrightDependenciesPath();
        Browser = await PlaywrightProvider.CreateBrowserAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Browser.CloseAsync();
    }

    public async Task<IBrowserContext> CreateContextAsync(BrowserNewContextOptions options)
    {
        var context = await Browser.NewContextAsync(options);
        ConfigureTimeouts(context);
        return context;
    }

    public static void ConfigureTimeouts(IBrowserContext context)
    {
        Assertions.SetDefaultExpectTimeout(TestConstants.DefaultTimeoutDuration);
        context.SetDefaultTimeout(TestConstants.DefaultTimeoutDuration);
        context.SetDefaultNavigationTimeout(TestConstants.DefaultTimeoutDuration);
    }

    public async Task GoToHomeAndWaitForDataGridLoad(IPage page)
    {
        await page.GotoAsync("/");
        await Assertions
            .Expect(page.GetByText(MockDashboardClient.TestResource1.DisplayName))
            .ToBeVisibleAsync();
    }
}
