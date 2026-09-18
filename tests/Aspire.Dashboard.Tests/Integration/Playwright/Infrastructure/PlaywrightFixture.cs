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
        // Default timeout of 5000 ms could time out on slow CI servers.
        Assertions.SetDefaultExpectTimeout(TestConstants.LongTimeoutDuration);

        PlaywrightProvider.DetectAndSetInstalledPlaywrightDependenciesPath();
        Browser = await PlaywrightProvider.CreateBrowserAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Browser.CloseAsync();
    }

    public async Task GoToHomeAndWaitForDataGridLoad(IPage page)
    {
        await GoToResourcesAsync(page);
        await Assertions
            .Expect(page.GetByText(MockDashboardClient.TestResource1.DisplayName))
            .ToBeVisibleAsync();
    }

    public async Task GoToResourcesAsync(IPage page)
    {
        // The HTTP root redirect uses the live AppHost client. Client-side navigation instead
        // exercises the selected resource client supplied by the browser fixture.
        await page.GotoAsync("/traces");
        await page.Locator("a[href='/']").First.ClickAsync();
    }
}
