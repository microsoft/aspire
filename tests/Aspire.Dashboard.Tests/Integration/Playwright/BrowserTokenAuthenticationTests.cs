// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Sockets;
using Aspire.TestUtilities;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Resources;
using Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;
using Aspire.Hosting;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Playwright;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration.Playwright;

[RequiresFeature(TestFeature.Playwright)]
public class BrowserTokenAuthenticationTests : PlaywrightTestsBase<BrowserTokenAuthenticationTests.BrowserTokenDashboardServerFixture>
{
    public class BrowserTokenDashboardServerFixture : DashboardServerFixture
    {
        public BrowserTokenDashboardServerFixture()
        {
            Configuration[DashboardConfigNames.DashboardFrontendAuthModeName.ConfigKey] = nameof(FrontendAuthMode.BrowserToken);
            Configuration[DashboardConfigNames.DashboardFrontendBrowserTokenName.ConfigKey] = "VALID_TOKEN";
        }
    }

    public sealed class BrowserTokenDashboardServerWithHttpAndHttpsFixture : DashboardServerFixture
    {
        public BrowserTokenDashboardServerWithHttpAndHttpsFixture()
        {
            Configuration[DashboardConfigNames.DashboardFrontendUrlName.ConfigKey] = $"https://localhost:{GetAvailablePort()};http://localhost:{GetAvailablePort()}";
            Configuration[DashboardConfigNames.DashboardFrontendAuthModeName.ConfigKey] = nameof(FrontendAuthMode.BrowserToken);
            Configuration[DashboardConfigNames.DashboardFrontendBrowserTokenName.ConfigKey] = "VALID_TOKEN";
        }

        private static int GetAvailablePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }

    public BrowserTokenAuthenticationTests(BrowserTokenDashboardServerFixture dashboardServerFixture)
        : base(dashboardServerFixture)
    {
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task BrowserToken_LoginPage_Success_RedirectToResources()
    {
        // Arrange
        await RunTestAsync(async page =>
        {
            // Act
            var response = await page.GotoAsync("/").DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
            var uri = new Uri(response!.Url);

            Assert.Equal("/login?returnUrl=%2F", uri.PathAndQuery);

            var tokenTextBox = page.GetByRole(AriaRole.Textbox);
            await tokenTextBox.FillAsync("VALID_TOKEN").DefaultTimeout(TestConstants.LongTimeoutTimeSpan);

            var submitButton = page.GetByRole(AriaRole.Button);
            await submitButton.ClickAsync().DefaultTimeout(TestConstants.LongTimeoutTimeSpan);

            // Wait for navigation to complete after successful login.
            // The page redirects from /login to / (resources page).
            await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/").DefaultTimeout(TestConstants.LongTimeoutTimeSpan);

            // Assert
            await Assertions
                .Expect(page.GetByText(MockDashboardClient.TestResource1.DisplayName))
                .ToBeVisibleAsync()
                .DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task BrowserToken_LoginPage_Failure_DisplayFailureMessage()
    {
        // Arrange
        await RunTestAsync(async page =>
        {
            // Act
            var response = await page.GotoAsync("/").DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
            var uri = new Uri(response!.Url);

            Assert.Equal("/login?returnUrl=%2F", uri.PathAndQuery);

            var tokenTextBox = page.GetByRole(AriaRole.Textbox);
            await tokenTextBox.FillAsync("INVALID_TOKEN").DefaultTimeout(TestConstants.LongTimeoutTimeSpan);

            var submitButton = page.GetByRole(AriaRole.Button);
            await submitButton.ClickAsync().DefaultTimeout(TestConstants.LongTimeoutTimeSpan);

            // Assert
            await Assertions
                .Expect(page.GetByText(Login.InvalidTokenErrorMessage))
                .ToBeVisibleAsync()
                .DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task BrowserToken_QueryStringToken_Success_RestrictToResources()
    {
        // Arrange
        await RunTestAsync(async page =>
        {
            // Act
            await page.GotoAsync("/login?t=VALID_TOKEN").DefaultTimeout(TestConstants.LongTimeoutTimeSpan);

            // Assert
            await Assertions
                .Expect(page.GetByText(MockDashboardClient.TestResource1.DisplayName))
                .ToBeVisibleAsync()
                .DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task BrowserToken_QueryStringToken_Failure_DisplayLoginPage()
    {
        // Arrange
        await RunTestAsync(async page =>
        {
            // Act
            await page.GotoAsync("/login?t=INVALID_TOKEN").DefaultTimeout(TestConstants.LongTimeoutTimeSpan);

            var submitButton = page.GetByRole(AriaRole.Button);
            var name = await submitButton.GetAttributeAsync("name").DefaultTimeout(TestConstants.LongTimeoutTimeSpan);

            // Assert
            Assert.Equal("submit-token", name);
        });
    }
}

[RequiresFeature(TestFeature.Playwright)]
public class BrowserTokenAuthenticationHttpAndHttpsTests : PlaywrightTestsBase<BrowserTokenAuthenticationTests.BrowserTokenDashboardServerWithHttpAndHttpsFixture>
{
    public BrowserTokenAuthenticationHttpAndHttpsTests(BrowserTokenAuthenticationTests.BrowserTokenDashboardServerWithHttpAndHttpsFixture dashboardServerFixture)
        : base(dashboardServerFixture)
    {
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task BrowserToken_QueryStringToken_HttpsThenHttp_WebKit_Success()
    {
        using var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        await using var browser = await LaunchWebKitAsync(playwright);

        await RunHttpsThenHttpAsync(browser);
    }

    private static async Task<IBrowser> LaunchWebKitAsync(IPlaywright playwright)
    {
        try
        {
            return await playwright.Webkit.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        }
        catch (PlaywrightException ex) when (IsWebKitBrowserUnavailable(ex))
        {
            Assert.Skip("Playwright WebKit is not available in this environment.");
            throw;
        }
    }

    private static bool IsWebKitBrowserUnavailable(PlaywrightException ex)
    {
        return ex.Message.Contains("Executable doesn't exist", StringComparison.Ordinal) ||
            ex.Message.Contains("Host system is missing dependencies", StringComparison.Ordinal);
    }

    private async Task RunHttpsThenHttpAsync(IBrowser browser)
    {
        var endpoints = DashboardServerFixture.DashboardApp.FrontendEndPointsAccessor
            .Select(accessor => accessor())
            .ToList();
        var httpsEndpoint = endpoints.Single(e => e.IsHttps);
        var httpEndpoint = endpoints.Single(e => !e.IsHttps);

        var httpsBaseUrl = httpsEndpoint.GetResolvedAddress(replaceIPAnyWithLocalhost: true);
        var httpBaseUrl = httpEndpoint.GetResolvedAddress(replaceIPAnyWithLocalhost: true);

        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true
        });
        try
        {
            var page = await context.NewPageAsync();
            try
            {
                await page.GotoAsync($"{httpsBaseUrl}/login?t=VALID_TOKEN").DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
                await Assertions
                    .Expect(page.GetByText(MockDashboardClient.TestResource1.DisplayName))
                    .ToBeVisibleAsync()
                    .DefaultTimeout(TestConstants.LongTimeoutTimeSpan);

                await page.GotoAsync($"{httpBaseUrl}/login?t=VALID_TOKEN").DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
                await Assertions
                    .Expect(page.GetByText(MockDashboardClient.TestResource1.DisplayName))
                    .ToBeVisibleAsync()
                    .DefaultTimeout(TestConstants.LongTimeoutTimeSpan);

                await page.GotoAsync($"{httpBaseUrl}/structuredlogs").DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
                Assert.Equal("/structuredlogs", new Uri(page.Url).AbsolutePath);
                await Assertions
                    .Expect(page.GetByRole(AriaRole.Button, new() { Name = "submit-token" }))
                    .Not
                    .ToBeVisibleAsync()
                    .DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
            }
            finally
            {
                await page.CloseAsync();
            }
        }
        finally
        {
            await context.DisposeAsync();
        }
    }
}
